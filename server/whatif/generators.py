"""Asset generators.

Pipeline per asset:  text prompt -> image -> background removal -> trimmed RGBA cut-out (billboard)
                                                   \\-> optional image-to-3D -> normalised GLB

Conventions every provider must respect (the headset relies on them):
- billboard PNG: transparent background, object touches the bottom edge (that is where it stands)
- GLB: Y up, object base at y = 0, centred on x/z, total height exactly 1 m
  (the client scales it to the planned real-world height)
"""

from __future__ import annotations

import hashlib
import io
import logging
import shlex
import subprocess
import tempfile
import re
import threading
from contextlib import contextmanager
import time
from pathlib import Path
from typing import Protocol

import numpy as np
from PIL import Image, ImageDraw

from .config import settings

log = logging.getLogger("whatif.generators")

# SDXL-Turbo runs without classifier-free guidance, so negative prompts have no effect.
# Asking for a *small* object in a large empty studio was the only wording that stopped it
# from filling the frame (tested: 6/8 complete trees/shrubs vs 0/8 with "fully visible").
PROMPT_PREFIX = "small "
PROMPT_SUFFIX = ", standing in the middle of a large empty white studio, wide shot, lots of empty space around it"


def _view(subject: str) -> str:
    """Still objects from a three-quarter angle, slightly from above: the 3D model sees their depth
    (rocks drawn straight from the side became slabs). Moving things keep their side view."""
    s = subject.lower()
    # coat pieces are seen from directly above (they lie flat), drapes from the front (they hang flat)
    keep = ("side view", "facing right", "from directly above", "top-down", "front view")
    return "" if any(k in s for k in keep) else ", three-quarter view from slightly above"


# --------------------------------------------------------------------------- GPU sharing


class GpuArbiter:
    """Keeps only one group of models on the GPU when they don't fit together.

    SDXL-Turbo + BiRefNet (~7 GB) and SPAR3D (~7 GB weights + ~3 GB while running) don't fit on
    a 16 GB GPU at the same time. Groups register callbacks that move their models between CPU
    and GPU; `use(name)` makes sure that group is on the GPU and holds a lock while it works.
    Models that fit alongside everything (TripoSR) don't register and never wait.
    """

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._groups: dict[str, list[tuple]] = {}
        self._active: str | None = None
        self._group_locks: dict[str, threading.Lock] = {}

    @property
    def swaps(self) -> bool:
        """True if groups take turns on the GPU (more than one registered)."""
        return len(self._groups) > 1

    def register(self, group: str, to_cpu, to_gpu, active: bool) -> None:
        with self._lock:
            self._groups.setdefault(group, []).append((to_cpu, to_gpu))
            if active:
                self._active = group

    @contextmanager
    def use(self, group: str):
        if len(self._groups) < 2:
            # Nothing to swap with (big GPU): one lock per group, so image and 3D work run at the
            # same time while each model is still used by one thread at a time.
            with self._group_locks.setdefault(group, threading.Lock()):
                yield
            return
        with self._lock:
            if group in self._groups and self._active != group:
                self._switch(group)
            yield

    def _switch(self, group: str) -> None:
        import torch

        t = time.perf_counter()
        for name, callbacks in self._groups.items():
            if name != group:
                for to_cpu, _ in callbacks:
                    to_cpu()
        torch.cuda.empty_cache()
        for _, to_gpu in self._groups[group]:
            to_gpu()
        self._active = group
        log.info("gpu: switched to %s in %.1f s", group, time.perf_counter() - t)

    @property
    def active(self) -> str | None:
        return self._active


gpu = GpuArbiter()


# --------------------------------------------------------------------------- image


class ImageProvider(Protocol):
    def generate(self, prompt: str, seed: int = 0) -> Image.Image: ...


class MockImageProvider:
    """Draws a simple coloured silhouette. Lets you test placement without a GPU."""

    def generate(self, prompt: str, seed: int = 0) -> Image.Image:
        h = int(hashlib.sha1(prompt.encode()).hexdigest(), 16)
        color = (60 + h % 160, 80 + (h >> 8) % 160, 60 + (h >> 16) % 160)
        img = Image.new("RGB", (512, 512), "white")
        d = ImageDraw.Draw(img)
        lower = prompt.lower()
        if any(w in lower for w in ("tree", "shrub", "plant", "ivy", "flower")):
            d.rectangle((236, 300, 276, 500), fill=(110, 75, 40))
            d.ellipse((96, 40, 416, 360), fill=color)
        else:
            d.rounded_rectangle((136, 120, 376, 500), radius=40, fill=color)
        d.text((150, 20), prompt[:40], fill=(0, 0, 0))
        return img


class SdxlTurboImageProvider:
    """Local text-to-image with diffusers (SDXL-Turbo, 4 steps: ~1 s on an RTX 3080 Laptop)."""

    def __init__(self) -> None:
        import torch
        from diffusers import AutoPipelineForText2Image

        self._torch = torch
        self._pipe = AutoPipelineForText2Image.from_pretrained(
            settings.image_model, torch_dtype=torch.float16, variant="fp16"
        ).to("cuda")
        self._pipe.set_progress_bar_config(disable=True)
        gpu.register("image", lambda: self._pipe.to("cpu"), lambda: self._pipe.to("cuda"), active=True)

    def generate(self, prompt: str, seed: int = 0) -> Image.Image:
        gen = self._torch.Generator("cuda").manual_seed(seed)
        return self._pipe(
            prompt=self.fit_prompt(prompt),
            num_inference_steps=settings.image_steps,
            guidance_scale=0.0,
            width=512,
            height=512,
            generator=gen,
        ).images[0]


    def fit_prompt(self, subject: str) -> str:
        """SDXL's text encoders read at most 77 tokens and silently drop the rest. Long subject
        descriptions used to push the framing suffix out, so objects filled the frame again.
        Shorten the subject (word by word) until prefix + subject + suffix fit."""
        tok = self._pipe.tokenizer
        limit = tok.model_max_length - 2  # begin/end tokens
        words = subject.split()
        while words:
            text = PROMPT_PREFIX + " ".join(words) + _view(subject) + PROMPT_SUFFIX
            if len(tok(text, add_special_tokens=False).input_ids) <= limit:
                return text
            words.pop()
        return PROMPT_PREFIX.strip() + _view(subject) + PROMPT_SUFFIX


_image_provider: ImageProvider | None = None


def image_provider() -> ImageProvider:
    global _image_provider
    if _image_provider is None:
        name = settings.image_provider
        if name == "mock":
            _image_provider = MockImageProvider()
        elif name == "sdxl_turbo":
            _image_provider = SdxlTurboImageProvider()
        else:
            raise ValueError(f"Unknown WHATIF_IMAGE_PROVIDER '{name}'")
    return _image_provider


# --------------------------------------------------------------------------- cut-out

class BiRefNetMatting:
    """BiRefNet (general) in PyTorch fp16 on the GPU.

    Why BiRefNet: u2net / isnet treat dense foliage as background (trees lose their crowns);
    BiRefNet keeps it. Why PyTorch fp16: the ONNX export needs > 6 GB VRAM in fp32 and does
    not fit next to SDXL-Turbo on a 16 GB GPU; it also shares torch's allocator this way.

    The model code comes from the authors' Hugging Face repo (trust_remote_code), pinned to a
    reviewed revision: model definition only, weights from safetensors, bb_pretrained=false.
    """

    REPO = "ZhengPeng7/BiRefNet"
    REVISION = "e2bf8e4460fc8fa32bba5ea4d94b3233d367b0e4"  # reviewed 2026-11-05
    MEAN = (0.485, 0.456, 0.406)
    STD = (0.229, 0.224, 0.225)

    def __init__(self) -> None:
        import torch
        from transformers import AutoModelForImageSegmentation

        self._torch = torch
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self._dtype = torch.float16 if self.device == "cuda" else torch.float32
        model = AutoModelForImageSegmentation.from_pretrained(
            self.REPO, revision=self.REVISION, trust_remote_code=True
        )
        self._model = model.to(self.device, self._dtype).eval()
        if self.device == "cuda":
            gpu.register("image", lambda: self._model.to("cpu"), lambda: self._model.to("cuda"), active=True)
        self._mean = torch.tensor(self.MEAN, device=self.device).view(1, 3, 1, 1)
        self._std = torch.tensor(self.STD, device=self.device).view(1, 3, 1, 1)

    def __call__(self, img: Image.Image) -> Image.Image:
        torch = self._torch
        rgb = img.convert("RGB")
        x = torch.from_numpy(np.asarray(rgb.resize((1024, 1024), Image.Resampling.BILINEAR)).copy())
        x = x.to(self.device).permute(2, 0, 1)[None].float() / 255.0
        x = ((x - self._mean) / self._std).to(self._dtype)
        with torch.inference_mode():
            pred = self._model(x)[-1].sigmoid()[0, 0].float().cpu().numpy()
        mask = Image.fromarray((pred * 255).astype(np.uint8), "L").resize(rgb.size, Image.Resampling.LANCZOS)
        out = rgb.convert("RGBA")
        out.putalpha(mask)
        return out


_matting = None


def _remove_background(img: Image.Image) -> Image.Image:
    global _matting
    if settings.remove_background:
        try:
            if _matting is None:  # loading the model takes seconds; reuse it
                if settings.matting_model == "birefnet":
                    _matting = BiRefNetMatting()
                    log.info("background removal: BiRefNet on %s", _matting.device)
                else:
                    from rembg import new_session, remove

                    session = new_session(settings.matting_model)
                    _matting = lambda im: remove(im, session=session).convert("RGBA")  # noqa: E731
            return _matting(img)
        except ImportError:
            log.warning("background removal model unavailable, using white key")
    return _white_key(img)


def _white_key(img: Image.Image) -> Image.Image:
    """Treat near-white pixels as background (works for the mock provider and for most
    "isolated on white" generations)."""
    rgba = np.array(img.convert("RGBA"))
    white = (rgba[..., :3].astype(int).min(axis=-1) > 235)
    rgba[white, 3] = 0
    return Image.fromarray(rgba, "RGBA")


def _edge_contact(alpha: np.ndarray, margin: int = 3) -> float:
    """Fraction of the image border covered by the object. The bottom edge counts too: an object
    touching it is usually cut off (a figure without feet), even though objects stand there.
    The cut-out is trimmed to its base afterwards, so a margin below costs nothing."""
    solid = alpha > 128
    borders = [solid[:margin, :], solid[-margin:, :], solid[:, :margin], solid[:, -margin:]]
    return float(sum(b.sum() for b in borders)) / float(sum(b.size for b in borders))


def make_cutout(img: Image.Image, pad: int = 4) -> Image.Image:
    """Remove background and trim so the object touches the bottom edge."""
    return _trim(_remove_background(img), pad)


def _keep_main_object(rgba: Image.Image, min_share: float = 0.25) -> Image.Image:
    """Drop stray props: keep connected regions at least `min_share` the size of the largest.
    The mask is dilated first so parts of one object (spokes, leaves) stay together."""
    from scipy import ndimage

    arr = np.array(rgba)
    solid = arr[..., 3] > 128
    labels, n = ndimage.label(ndimage.binary_dilation(solid, iterations=6))
    if n <= 1:
        return rgba
    sizes = ndimage.sum(solid, labels, index=range(1, n + 1))
    keep = [i + 1 for i, s in enumerate(sizes) if s >= min_share * sizes.max()]
    arr[~np.isin(labels, keep), 3] = 0
    return Image.fromarray(arr, "RGBA")


def _trim(rgba: Image.Image, pad: int = 4) -> Image.Image:
    rgba = _keep_main_object(rgba)
    alpha = np.array(rgba)[..., 3]
    ys, xs = np.nonzero(alpha > 16)
    if len(xs) == 0:
        raise ValueError("background removal left an empty image")
    x0, x1 = max(xs.min() - pad, 0), min(xs.max() + pad + 1, rgba.width)
    y0, y1 = max(ys.min() - pad, 0), ys.max() + 1  # no padding below: base sits on the ground
    return rgba.crop((x0, y0, x1, y1))


# Start-up state for /health: "loading" until the warm-up finished, then "ready" or "failed".
models_state = "loading"


def warm_up() -> None:
    """Load image + matting models and run one tiny generation (first GPU calls are slow)."""
    global models_state
    try:
        t = time.perf_counter()
        generate_cutout("a wooden chair")
        mesh_provider()  # loads TripoSR if configured
        log.info("models ready after %.1f s", time.perf_counter() - t)
        models_state = "ready"
    except Exception:  # noqa: BLE001 - a failed warm-up resurfaces on the first real job
        log.exception("warm-up failed")
        models_state = "failed"


def generate_cutout(prompt: str, extra: list | None = None) -> tuple[Image.Image, int]:
    """Generate until the object is not cut off at the sides/top (max N attempts).
    `extra`: real pictures (web) that compete with the generated ones.
    Returns the best cut-out and the number of attempts used."""
    provider = image_provider()
    with gpu.use("image"):
        return _generate_cutout(provider, prompt, extra or [])


def _coverage(rgba: Image.Image) -> float:
    return float((np.array(rgba)[..., 3] > 128).mean())


_PLANT = re.compile(r"\b(plants?|trees?|palms?|ferns?|bush(es)?|shrubs?|monstera|banana|bamboo|cact(us|i)|"
                    r"orchids?|flowers?|grass(es)?|reeds?|agaves?|aloe|succulents?|saplings?|bromeliads?)\b", re.I)
_CONTAINER = re.compile(r"\b(pots?|potted|planters?|vases?|containers?|tubs?|bowls?|baskets?|indoor|house ?plants?|"
                        r"topf|blumentopf|kübel|vase)\b", re.I)


def wild_plant(prompt: str) -> bool:
    """A plant the description doesn't put into a pot or vase."""
    return bool(_PLANT.search(prompt)) and not _CONTAINER.search(prompt)


def potted(scorer, rgba: Image.Image) -> float:
    """How sure CLIP is that the picture shows a plant in a pot (0-1). The image model draws a lone
    plant as a potted houseplant, even for a jungle: such pictures lose against pot-free ones."""
    pot = scorer.score(rgba, "a photo of a plant in a flower pot")
    wild = scorer.score(rgba, "a photo of a wild plant growing out of the ground, roots in the soil")
    return float(1.0 / (1.0 + np.exp(-100.0 * (pot - wild))))


def _generate_cutout(provider, prompt: str, extra: list | None = None) -> tuple[Image.Image, int]:
    """Best of a few seeds. An attempt only counts if background removal kept an object: an empty
    result touches no border and used to be taken as perfect, then failed - and the object never
    got a cut-out (seen with a grocery shelf that filled the whole frame)."""
    best, best_contact, best_quality, last_image = None, 2.0, -1e9, None
    candidates = settings.image_candidates
    scorer = _clip_scorer() if candidates > 1 else None
    attempts = max(1, settings.image_attempts, candidates)
    no_pot = scorer is not None and wild_plant(prompt)
    for i, picture in enumerate(extra or []):  # real pictures first, judged the same way (+ a small bonus)
        rgba = _keep_main_object(_remove_background(picture))
        if _coverage(rgba) < 0.005:
            continue
        contact = _edge_contact(np.array(rgba)[..., 3])
        match = scorer.score(rgba, prompt) if scorer is not None else 0.0
        quality = match - 0.3 * contact + settings.web_bonus
        log.info("web picture %d: match %.3f, cut off %.2f", i + 1, match, contact)
        if quality > best_quality:
            best, best_contact, best_quality = rgba, contact, quality
    for seed in range(attempts):
        last_image = provider.generate(prompt, seed=seed)
        rgba = _keep_main_object(_remove_background(last_image))
        if _coverage(rgba) < 0.005:
            log.info("cut-out attempt %d removed everything, retrying", seed + 1)
            continue
        contact = _edge_contact(np.array(rgba)[..., 3])
        if scorer is None:
            quality = -contact
        else:
            # CLIP similarity (~0.2-0.35) decides what matches the prompt ("blue", "hat");
            # being cut off costs as much as a clearly worse match
            match = scorer.score(rgba, prompt)
            quality = match - 0.3 * contact
            pot = potted(scorer, rgba) if no_pot else 0.0
            quality -= 0.1 * pot  # a pot costs about as much as a clearly worse match
            log.info("cut-out attempt %d: match %.3f, cut off %.2f%s", seed + 1, match, contact,
                     f", pot {pot:.2f}" if no_pot else "")
        if quality > best_quality:
            best, best_contact, best_quality = rgba, contact, quality
        enough = seed + 1 >= (candidates if scorer is not None else 1)
        if enough and best_contact < 0.01:
            return _trim(best), seed + 1
    if best is None:
        # Every attempt came back empty: key out the white studio background instead, and if even
        # that leaves nothing, show the whole image rather than nothing at all.
        log.warning("background removal failed for %r; using the white-key fallback", prompt)
        best = _white_key(last_image)
        if _coverage(best) < 0.005:
            best = last_image.convert("RGBA")
    return _trim(best), attempts


class _ClipScorer:
    """How well a cut-out matches its prompt (cosine similarity, OpenAI CLIP ViT-B/32, ~0.6 GB)."""

    def __init__(self) -> None:
        import clip
        import torch

        self._torch = torch
        self._device = "cuda" if torch.cuda.is_available() else "cpu"
        self._model, self._preprocess = clip.load("ViT-B/32", device=self._device)
        self._model.eval()
        self._clip = clip
        self._lock = threading.Lock()

    def text_embedding(self, prompt: str) -> list[float]:
        """Normalised CLIP text embedding (what a description means), for the object library."""
        torch = self._torch
        with self._lock, torch.no_grad():
            txt = self._clip.tokenize([prompt], truncate=True).to(self._device)
            b = self._model.encode_text(txt).float()
            b = b / b.norm(dim=-1, keepdim=True)
            return b[0].cpu().tolist()

    def text_embeddings(self, prompts: list[str]) -> np.ndarray:
        """Normalised CLIP text embeddings of many descriptions at once (the Objaverse index)."""
        torch = self._torch
        with self._lock, torch.no_grad():
            txt = self._clip.tokenize(prompts, truncate=True).to(self._device)
            b = self._model.encode_text(txt).float()
            b = b / b.norm(dim=-1, keepdim=True)
            return b.cpu().numpy().astype(np.float16)

    def _image_embedding(self, img: Image.Image):
        torch = self._torch
        white = Image.new("RGB", img.size, "white")
        white.paste(img, mask=img.split()[3] if img.mode == "RGBA" else None)
        with self._lock, torch.no_grad():
            a = self._model.encode_image(self._preprocess(white).unsqueeze(0).to(self._device)).float()
        return a / a.norm(dim=-1, keepdim=True)

    def picture_similarity(self, a: Image.Image, b: Image.Image) -> float:
        """How alike two pictures look (cosine of CLIP image embeddings, ~0.7 unrelated - 1.0 same)."""
        return float((self._image_embedding(a) * self._image_embedding(b)).sum())

    def score(self, rgba: Image.Image, prompt: str) -> float:
        white = Image.new("RGB", rgba.size, "white")
        white.paste(rgba, mask=rgba.split()[3])
        torch = self._torch
        with self._lock, torch.no_grad():
            img = self._preprocess(white).unsqueeze(0).to(self._device)
            txt = self._clip.tokenize([prompt], truncate=True).to(self._device)
            a = self._model.encode_image(img).float()
            b = self._model.encode_text(txt).float()
            a = a / a.norm(dim=-1, keepdim=True)
            b = b / b.norm(dim=-1, keepdim=True)
            return float((a * b).sum())


_clip_state: dict = {}
_clip_lock = threading.Lock()


def _clip_scorer() -> _ClipScorer | None:
    """Loaded on first use; None (with one warning) if CLIP isn't installed or fails to load."""
    with _clip_lock:
        if "scorer" not in _clip_state:
            try:
                t = time.perf_counter()
                _clip_state["scorer"] = _ClipScorer()
                log.info("CLIP scorer loaded in %.1f s", time.perf_counter() - t)
            except Exception as e:  # noqa: BLE001 - selection then falls back to "least cut off"
                log.warning("CLIP not available (%s): cut-outs are picked by framing only", e)
                _clip_state["scorer"] = None
        return _clip_state["scorer"]


# --------------------------------------------------------------------------- 3D


class MeshProvider(Protocol):
    def generate(self, cutout_png: Path, out_glb: Path, max_faces: int | None = None) -> None: ...


class CrossQuadMeshProvider:
    """Two crossed, textured quads - the classic game-engine trick for vegetation.

    Instant and surprisingly convincing for plants; a cheap stand-in until a real
    image-to-3D model is configured.
    """

    def generate(self, cutout_png: Path, out_glb: Path, max_faces: int | None = None) -> None:
        import trimesh

        img = Image.open(cutout_png).convert("RGBA")
        w = img.width / img.height  # height is 1 m
        hw = w / 2
        verts, faces, uvs = [], [], []
        for axis in (0, 1):  # quad in XY plane, then in ZY plane
            base = len(verts)
            for (u, v) in ((0, 0), (1, 0), (1, 1), (0, 1)):
                x = -hw + u * w
                verts.append((x, v, 0.0) if axis == 0 else (0.0, v, x))
                uvs.append((u, v))
            faces += [(base, base + 1, base + 2), (base, base + 2, base + 3)]
        material = trimesh.visual.material.PBRMaterial(
            baseColorTexture=img, alphaMode="MASK", alphaCutoff=0.5, doubleSided=True,
            metallicFactor=0.0, roughnessFactor=1.0,
        )
        mesh = trimesh.Trimesh(
            vertices=np.array(verts), faces=np.array(faces), process=False,
            visual=trimesh.visual.TextureVisuals(uv=np.array(uvs), material=material),
        )
        out_glb.write_bytes(trimesh.Scene(mesh).export(file_type="glb"))
        finalize_glb(out_glb, settings.mesh_unlit)


class CommandMeshProvider:
    """Runs any external image-to-3D tool (Stable Fast 3D, TRELLIS, Hunyuan3D, ...).

    WHATIF_MESH_COMMAND must contain {input} and {output_dir}; the tool has to write
    a .glb somewhere below output_dir. The result is normalised afterwards.
    """

    def generate(self, cutout_png: Path, out_glb: Path, max_faces: int | None = None) -> None:
        if not settings.mesh_command:
            raise ValueError("WHATIF_MESH_COMMAND is not set")
        with tempfile.TemporaryDirectory() as tmp:
            cmd = settings.mesh_command.format(input=str(cutout_png), output_dir=tmp)
            subprocess.run(shlex.split(cmd, posix=False), check=True, timeout=settings.mesh_timeout_s,
                           capture_output=True)
            glbs = sorted(Path(tmp).rglob("*.glb"))
            if not glbs:
                raise RuntimeError("mesh command produced no .glb")
            normalize_glb(glbs[0], out_glb)


class TripoSRMeshProvider:
    """Single image -> textured (vertex-coloured) mesh with TripoSR, in-process on the GPU.

    TripoSR (Stability AI + Tripo, MIT) is used from a clone of the official repository in
    server/third_party/TripoSR (pinned commit, see README). Its only compiled dependency,
    torchmcubes, is replaced by scikit-image's marching cubes, so nothing has to be built.
    """

    _model = None
    _lock = threading.Lock()

    def __init__(self) -> None:
        with TripoSRMeshProvider._lock:
            if TripoSRMeshProvider._model is None:
                TripoSRMeshProvider._model = self._load()

    _VIT_RENAMES = [
        (".encoder.layer.", ".layers."),
        (".attention.attention.query.", ".attention.q_proj."),
        (".attention.attention.key.", ".attention.k_proj."),
        (".attention.attention.value.", ".attention.v_proj."),
        (".attention.output.dense.", ".attention.o_proj."),
        (".intermediate.dense.", ".mlp.fc1."),
        (".output.dense.", ".mlp.fc2."),
    ]

    @staticmethod
    def _rename_vit_key(key: str) -> str:
        if not key.startswith("image_tokenizer.model.encoder.layer."):
            return key
        for old, new in TripoSRMeshProvider._VIT_RENAMES:  # order matters: attention.output before output
            key = key.replace(old, new)
        return key

    @staticmethod
    def _load():
        import sys
        import types

        import torch

        repo = Path(settings.triposr_dir)
        if not (repo / "tsr").is_dir():
            raise RuntimeError(f"TripoSR not found in {repo} (see README: real 3D)")
        sys.path.insert(0, str(repo))

        def marching_cubes(volume, threshold):
            from skimage.measure import marching_cubes as mc

            v, f, _, _ = mc(volume.detach().float().cpu().numpy(), threshold)
            # torchmcubes returns (x, y, z) = reversed array axes
            return torch.from_numpy(v[:, ::-1].copy()).float(), torch.from_numpy(f.astype(np.int64))

        sys.modules.setdefault("torchmcubes", types.SimpleNamespace(marching_cubes=marching_cubes))
        from huggingface_hub import hf_hub_download
        from omegaconf import OmegaConf
        from tsr.system import TSR

        # Same as TSR.from_pretrained, plus a key rename: the checkpoint uses the old
        # transformers ViT parameter names, current transformers versions use new ones.
        cfg = OmegaConf.load(hf_hub_download("stabilityai/TripoSR", "config.yaml"))
        OmegaConf.resolve(cfg)
        model = TSR(cfg)
        ckpt = torch.load(hf_hub_download("stabilityai/TripoSR", "model.ckpt"), map_location="cpu")
        expected = set(model.state_dict())
        if not set(ckpt) <= expected:
            ckpt = {TripoSRMeshProvider._rename_vit_key(k): v for k, v in ckpt.items()}
        model.load_state_dict(ckpt)  # strict: fails loudly if the mapping is incomplete
        model.renderer.set_chunk_size(settings.triposr_chunk_size)
        return model.to("cuda" if torch.cuda.is_available() else "cpu")

    def generate(self, cutout_png: Path, out_glb: Path, max_faces: int | None = None) -> None:
        import torch
        import trimesh
        from tsr.utils import resize_foreground

        model = TripoSRMeshProvider._model
        device = next(model.parameters()).device
        img = resize_foreground(Image.open(cutout_png).convert("RGBA"), 0.85)
        a = np.asarray(img, dtype=np.float32) / 255.0
        rgb = a[..., :3] * a[..., 3:4] + 0.5 * (1.0 - a[..., 3:4])  # grey background, as in training
        texture_size = settings.mesh_texture_size
        with torch.no_grad():
            codes = model([Image.fromarray((rgb * 255).astype(np.uint8))], device=str(device))
            mesh = model.extract_mesh(codes, texture_size <= 0, resolution=settings.triposr_resolution)[0]
            orient_outward(mesh)  # skimage's marching cubes winds faces the other way round
            if texture_size > 0:
                # Simplify the geometry first, then bake colours from the triplane into a texture:
                # the model stays light while colours stay sharp.
                limit = min(settings.mesh_max_faces, max_faces or settings.mesh_max_faces)
                if len(mesh.faces) > limit:
                    mesh = mesh.simplify_quadric_decimation(face_count=limit)
                mesh = self._bake_texture(model, codes[0], mesh, texture_size)
            else:
                mesh = decimate_vertex_colored(mesh, min(settings.mesh_max_faces, max_faces or settings.mesh_max_faces))
                mesh.visual.vertex_colors = srgb_to_linear_u8(np.asarray(mesh.visual.vertex_colors))
        if device.type == "cuda":
            torch.cuda.empty_cache()  # leave room for SDXL-Turbo on the next job

        # TripoSR is Z-up; glTF is Y-up (same transform as TripoSR's own demo) ...
        mesh.apply_transform(trimesh.transformations.rotation_matrix(-np.pi / 2, [1, 0, 0]))
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi / 2, [0, 1, 0]))
        # ... which leaves the side seen in the source image facing -Z. Turn it to +Z, glTF's
        # "front"; the headset relies on this to face sofas into the room and animals at the user.
        # (glTFast mirrors X when importing, so +Z stays the front in Unity.)
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi, [0, 1, 0]))
        with tempfile.TemporaryDirectory() as tmp:
            raw = Path(tmp) / "raw.glb"
            raw.write_bytes(trimesh.Scene(mesh).export(file_type="glb"))
            normalize_glb(raw, out_glb)


    @staticmethod
    def _bake_texture(model, scene_code, mesh, size: int):
        """UV-unwrap `mesh` and fill a size x size texture by querying TripoSR's colour field
        at the 3D position of every texel. Pure numpy/PIL rasterisation, no OpenGL needed."""
        import torch
        import trimesh
        import xatlas
        from PIL import ImageDraw
        from scipy import ndimage

        vmap, faces, uv = xatlas.parametrize(mesh.vertices.astype(np.float32), mesh.faces.astype(np.uint32))
        verts = mesh.vertices[vmap]
        faces = faces.astype(np.int64)

        # 1) rasterise triangle ids into UV space (id + 1 encoded in RGB, 0 = empty)
        ids = Image.new("RGB", (size, size), (0, 0, 0))
        draw = ImageDraw.Draw(ids)
        px = np.stack([uv[:, 0] * size, (1.0 - uv[:, 1]) * size], axis=1)  # image rows go down
        for i, f in enumerate(faces):
            n = i + 1
            draw.polygon([tuple(px[f[0]]), tuple(px[f[1]]), tuple(px[f[2]])],
                         fill=(n & 255, (n >> 8) & 255, (n >> 16) & 255))
        idimg = np.asarray(ids).astype(np.int64)
        tri = idimg[..., 0] | (idimg[..., 1] << 8) | (idimg[..., 2] << 16)
        ys, xs = np.nonzero(tri)
        t = tri[ys, xs] - 1

        # 2) barycentric coordinates of each texel centre -> 3D position
        a, b, c = px[faces[t, 0]], px[faces[t, 1]], px[faces[t, 2]]
        p = np.stack([xs + 0.5, ys + 0.5], axis=1)
        v0, v1, v2 = b - a, c - a, p - a
        d00, d01, d11 = (v0 * v0).sum(1), (v0 * v1).sum(1), (v1 * v1).sum(1)
        d20, d21 = (v2 * v0).sum(1), (v2 * v1).sum(1)
        den = d00 * d11 - d01 * d01
        den = np.where(np.abs(den) < 1e-12, 1e-12, den)
        w1 = np.clip((d11 * d20 - d01 * d21) / den, 0, 1)
        w2 = np.clip((d00 * d21 - d01 * d20) / den, 0, 1)
        w0 = np.clip(1 - w1 - w2, 0, 1)
        pos = (verts[faces[t, 0]] * w0[:, None] + verts[faces[t, 1]] * w1[:, None]
               + verts[faces[t, 2]] * w2[:, None])

        # 3) query colours (chunked by the renderer) and write the texture
        device = scene_code.device
        with torch.no_grad():
            col = model.renderer.query_triplane(
                model.decoder, torch.from_numpy(pos).float().to(device), scene_code
            )["color"].clamp(0, 1).cpu().numpy()
        tex = np.zeros((size, size, 3), dtype=np.uint8)
        tex[ys, xs] = (col * 255).round().astype(np.uint8)

        # 4) fill empty texels with the nearest colour so seams don't bleed black
        _, (iy, ix) = ndimage.distance_transform_edt(tri == 0, return_indices=True)
        tex = tex[iy, ix]

        material = trimesh.visual.material.PBRMaterial(
            baseColorTexture=Image.fromarray(tex, "RGB"), metallicFactor=0.0, roughnessFactor=1.0
        )
        return trimesh.Trimesh(vertices=verts, faces=faces, process=False,
                               visual=trimesh.visual.TextureVisuals(uv=uv, material=material))


class Spar3DMeshProvider:
    """Single image -> textured PBR mesh with SPAR3D (Stability AI), in-process on the GPU.

    Code: clone of the official repository in server/third_party/SPAR3D (pinned commit, see
    README); weights are gated on Hugging Face (request access + `hf auth login`).
    Its texture baker is built without the CUDA Toolkit, i.e. CPU-only, so baking runs on the
    CPU: inputs are moved there and results back. SPAR3D predicts lighting-free albedo plus
    metallic/roughness, so its models are exported lit (not unlit like TripoSR's).
    """

    _model = None
    _lock = threading.Lock()

    def __init__(self) -> None:
        with Spar3DMeshProvider._lock:
            if Spar3DMeshProvider._model is None:
                Spar3DMeshProvider._model = self._load()

    @staticmethod
    def _load():
        import sys

        import torch

        repo = Path(settings.spar3d_dir)
        if not (repo / "spar3d").is_dir():
            raise RuntimeError(f"SPAR3D not found in {repo} (see README: SPAR3D)")
        sys.path.insert(0, str(repo))

        # spar3d.utils imports its own background remover at module level; we pass cut-outs that
        # BiRefNet already freed, so a stand-in avoids installing another package and model.
        class _NoRemover:
            def __init__(self, *args, **kwargs):
                raise RuntimeError("transparent_background is not installed; cut-outs are made by BiRefNet")

        import types

        sys.modules.setdefault("transparent_background", types.SimpleNamespace(Remover=_NoRemover))

        # SPAR3D's bundled DINOv2 imports a helper that transformers 5 removed (only used for
        # pruning attention heads, never at inference). Restore it (same code as transformers 4.x).
        import transformers.pytorch_utils as tpu

        if not hasattr(tpu, "find_pruneable_heads_and_indices"):
            def find_pruneable_heads_and_indices(heads, n_heads, head_size, already_pruned_heads):
                mask = torch.ones(n_heads, head_size)
                heads = set(heads) - already_pruned_heads
                for head in heads:
                    head = head - sum(1 if h < head else 0 for h in already_pruned_heads)
                    mask[head] = 0
                mask = mask.view(-1).contiguous().eq(1)
                index = torch.arange(len(mask))[mask].long()
                return heads, index

            tpu.find_pruneable_heads_and_indices = find_pruneable_heads_and_indices

        # transformers 5 also removed PreTrainedModel.get_head_mask, which the bundled DINOv2 calls.
        import spar3d.models.tokenizers.dinov2 as spar_dinov2

        base = spar_dinov2.Dinov2PreTrainedModel
        if not hasattr(base, "get_head_mask"):
            def get_head_mask(self, head_mask, num_hidden_layers, is_attention_chunked=False):
                if head_mask is None:
                    return [None] * num_hidden_layers
                if head_mask.dim() == 1:
                    head_mask = head_mask[None, None, :, None, None].expand(num_hidden_layers, -1, -1, -1, -1)
                elif head_mask.dim() == 2:
                    head_mask = head_mask[:, None, :, None, None]
                head_mask = head_mask.to(dtype=self.dtype)
                if is_attention_chunked:
                    head_mask = head_mask.unsqueeze(-1)
                return head_mask

            base.get_head_mask = get_head_mask
        from spar3d.system import SPAR3D

        model = SPAR3D.from_pretrained(
            "stabilityai/stable-point-aware-3d",
            config_name="config.yaml",
            weight_name="model.safetensors",
            low_vram_mode=settings.spar3d_low_vram,
        )
        model.eval()
        _bake_on_cpu(model.baker)
        if not torch.cuda.is_available():
            return model
        if settings.spar3d_low_vram or not settings.gpu_swap:
            # low VRAM: fits next to the image models, but ~30-40 s per model;
            # no swapping: a big GPU keeps everything loaded
            model.to("cuda")
        else:
            # Swapped in and out by the GPU arbiter (~7 s per model, ~5 s per swap).
            model.to("cpu")
            gpu.register("spar3d", lambda: model.to("cpu"), lambda: model.to("cuda"), active=False)
        return model

    def generate(self, cutout_png: Path, out_glb: Path, max_faces: int | None = None) -> None:
        import torch
        import trimesh
        from spar3d.utils import foreground_crop

        model = Spar3DMeshProvider._model
        with gpu.use("spar3d"):
            self._generate(model, cutout_png, out_glb, max_faces)

    def _generate(self, model, cutout_png: Path, out_glb: Path, max_faces: int | None = None) -> None:
        import torch
        import trimesh
        from spar3d.utils import foreground_crop

        device = next(model.parameters()).device
        image = foreground_crop(Image.open(cutout_png).convert("RGBA"), crop_ratio=1.3)
        autocast = torch.autocast(device_type="cuda", dtype=torch.bfloat16) if device.type == "cuda" \
            else torch.autocast(device_type="cpu", enabled=False)
        with torch.no_grad(), autocast:
            # lighter model for fillers: SPAR3D remeshes to a vertex budget before baking the
            # texture, so it stays sharp (needs gpytoolbox; without it the full model is kept)
            remesh, vertices = ("triangle", max(500, max_faces // 2)) if max_faces and _has_gpytoolbox() else ("none", -1)
            mesh, _ = model.run_image(
                [image], bake_resolution=settings.spar3d_texture_size, remesh=remesh, vertex_count=vertices
            )
        if isinstance(mesh, list):
            mesh = mesh[0]
        if device.type == "cuda":
            torch.cuda.empty_cache()
        # Orientation is applied by settings so it can be corrected without code changes
        # once checked visually (front must face +Z, up must be +Y).
        for axis, deg in settings.spar3d_rotations:
            vec = {"x": [1, 0, 0], "y": [0, 1, 0], "z": [0, 0, 1]}[axis]
            mesh.apply_transform(trimesh.transformations.rotation_matrix(np.radians(deg), vec))
        with tempfile.TemporaryDirectory() as tmp:
            raw = Path(tmp) / "raw.glb"
            raw.write_bytes(trimesh.Scene(mesh).export(file_type="glb"))
            normalize_glb(raw, out_glb, unlit=False, keep_pbr=True)


def _bake_on_cpu(baker) -> None:
    """Make a CPU-only TextureBaker accept GPU tensors: move inputs to the CPU, results back.
    (The baker is a torch Module, so its methods are wrapped in place instead of replacing it.)"""
    rasterize, interpolate = baker.rasterize, baker.interpolate

    def rasterize_cpu(uv, face_indices, bake_resolution):
        return rasterize(uv.float().cpu(), face_indices.cpu(), bake_resolution).to(uv.device)

    def interpolate_cpu(attr, rast, face_indices):
        return interpolate(attr.float().cpu(), rast.cpu(), face_indices.cpu()).to(attr.device)

    baker.rasterize = rasterize_cpu
    baker.interpolate = interpolate_cpu


def orient_outward(mesh) -> None:
    """Flip the mesh if most faces point towards its centre (renderers cull those faces,
    so you would see the inside). Works on open meshes too, unlike a signed-volume check."""
    # Normals from the triangles themselves: trimesh 5.2's invert() leaves stale cached normals.
    tri = mesh.vertices[mesh.faces]
    normals = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    outward = ((tri.mean(axis=1) - mesh.vertices.mean(axis=0)) * normals).sum(axis=1)
    if np.mean(outward > 0) < 0.5:
        mesh.faces = mesh.faces[:, ::-1]  # reverse winding; resets trimesh's caches


def srgb_to_linear_u8(colors: np.ndarray) -> np.ndarray:
    """glTF vertex colours (COLOR_0) are linear; generated colours are sRGB."""
    c = colors.astype(np.float32) / 255.0
    rgb = c[..., :3]
    c[..., :3] = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    return (c * 255).round().astype(np.uint8)


def finalize_glb(path: Path, unlit: bool, keep_pbr: bool = False) -> None:
    """Patch the GLB's JSON: every primitive gets a non-metallic material (glTF's default
    material is fully metallic, which renders almost black without reflections) and,
    optionally, KHR_materials_unlit so models look like the billboards they were made from."""
    import json
    import struct

    data = path.read_bytes()
    json_len = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20:20 + json_len])
    materials = gltf.setdefault("materials", [])
    default_index = None
    for mesh in gltf.get("meshes", []):
        for prim in mesh.get("primitives", []):
            if "material" not in prim:
                if default_index is None:
                    materials.append({"name": "whatif_default",
                                      "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1]}})
                    default_index = len(materials) - 1
                prim["material"] = default_index
    for m in materials:
        pbr = m.setdefault("pbrMetallicRoughness", {})
        # matte: glossy maps made highlights sparkle with every small head movement on the headset
        pbr.pop("metallicRoughnessTexture", None)
        pbr["metallicFactor"] = 0.0
        pbr["roughnessFactor"] = max(0.85, float(pbr.get("roughnessFactor", 1.0))) if keep_pbr else 1.0
        if "normalTexture" in m:
            m["normalTexture"]["scale"] = min(0.5, float(m["normalTexture"].get("scale", 1.0)))
        if unlit:
            m.setdefault("extensions", {})["KHR_materials_unlit"] = {}
    if unlit and materials:
        used = gltf.setdefault("extensionsUsed", [])
        if "KHR_materials_unlit" not in used:
            used.append("KHR_materials_unlit")
    new_json = json.dumps(gltf, separators=(",", ":")).encode()
    new_json += b" " * (-len(new_json) % 4)
    rest = data[20 + json_len:]
    header = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(new_json) + len(rest))
    path.write_bytes(header + struct.pack("<II", len(new_json), 0x4E4F534A) + new_json + rest)


def decimate_vertex_colored(mesh, max_faces: int):
    """Reduce to at most `max_faces` triangles (the headset renders many instances), keeping
    vertex colours by copying them from the nearest original vertex."""
    import trimesh
    from scipy.spatial import cKDTree

    if max_faces <= 0 or len(mesh.faces) <= max_faces:
        return mesh
    colors = np.asarray(mesh.visual.vertex_colors)
    small = mesh.simplify_quadric_decimation(face_count=max_faces)
    _, nearest = cKDTree(mesh.vertices).query(small.vertices)
    return trimesh.Trimesh(vertices=small.vertices, faces=small.faces, vertex_colors=colors[nearest], process=False)


def level_and_clean(scene, max_tilt_deg: float = 25.0, min_tilt_deg: float = 2.0) -> float:
    """Stand the model upright on a flat floor and drop loose fragments below it.

    Image-to-3D models inherit the camera elevation of the source image (product shots are taken
    from slightly above), so they come out tilted by a few to ~15 degrees: tables on two legs,
    leaning sofas. Among the model's stable resting poses (convex-hull faces it could rest on,
    computed on its main parts only), take the most likely one within `max_tilt_deg` of the
    current "down" and apply only that tilt, so the facing direction is kept. Small loose
    fragments are ignored for this, and removed if they hang below the base (they would
    otherwise become the lowest point and make the object float). Returns the correction in degrees.
    """
    import trimesh

    if len(scene.geometry) != 1:
        return 0.0
    name, mesh = next(iter(scene.geometry.items()))
    if len(mesh.faces) < 4:
        return 0.0

    # Textured meshes duplicate vertices along UV seams, so parts must be found by vertex
    # position, not index: merge on a geometry-only copy (face order is unchanged).
    geom = trimesh.Trimesh(vertices=mesh.vertices, faces=mesh.faces, process=False)
    geom.merge_vertices()
    labels = trimesh.graph.connected_component_labels(geom.face_adjacency, node_count=len(geom.faces))
    sizes = np.bincount(labels)
    main = sizes >= 0.1 * sizes.max()
    support = mesh.submesh([np.nonzero(main[labels])[0]], append=True)

    # --- tilt (work in Z-up, as trimesh's stable poses expect)
    yz = trimesh.transformations.rotation_matrix(np.pi / 2, [1, 0, 0])
    sup_z = support.copy()
    sup_z.apply_transform(yz)
    tilt = 0.0
    try:
        hull = sup_z.convex_hull
        poses, probs = trimesh.poses.compute_stable_poses(sup_z, center_mass=hull.center_mass, threshold=0.0)
    except Exception:  # noqa: BLE001 - degenerate hulls: leave the model as it is
        poses, probs = [], []
    best = None
    for T, prob in zip(poses, probs):
        up = T[:3, :3].T @ np.array([0.0, 0.0, 1.0])  # model direction that ends up pointing up
        deg = float(np.degrees(np.arccos(np.clip(up[2], -1.0, 1.0))))
        if deg <= max_tilt_deg and (best is None or prob > best[1]):
            best = (up, prob, deg)
    if best is not None and best[2] >= min_tilt_deg:
        up = best[0]
        R = trimesh.transformations.rotation_matrix(np.arccos(np.clip(up[2], -1, 1)), np.cross(up, [0, 0, 1.0]))
        scene.apply_transform(np.linalg.inv(yz) @ R @ yz)
        support.apply_transform(np.linalg.inv(yz) @ R @ yz)
        tilt = best[2]

    # --- drop loose fragments below the base of the main parts
    mesh = scene.geometry[name]
    world = scene.graph.get(name)[0]
    verts = trimesh.transform_points(mesh.vertices, world)
    base = support.bounds[0][1]
    centre_y = np.zeros(len(sizes))
    np.add.at(centre_y, labels, verts[mesh.faces].mean(axis=1)[:, 1])
    centre_y /= np.maximum(sizes, 1)
    tiny = sizes < 0.015 * sizes.sum()  # crumbs (e.g. a loose "bean" next to a sandbag), wherever they are
    drop = (~main) & ((centre_y < base + 0.02 * (support.bounds[1][1] - base)) | tiny)
    if drop.any():
        mesh.update_faces(~drop[labels])
        mesh.remove_unreferenced_vertices()
    return tilt


def normalize_glb(src: Path, dst: Path, unlit: bool | None = None, keep_pbr: bool = False) -> None:
    """Scale to 1 m height, base at y=0, centred on x/z."""
    import trimesh

    scene = trimesh.load(src, force="scene")
    if settings.mesh_auto_level:
        level_and_clean(scene)
    (minx, miny, minz), (maxx, maxy, maxz) = scene.bounds
    height = maxy - miny
    if height <= 0:
        raise ValueError("degenerate mesh")
    s = 1.0 / height
    t = np.eye(4)
    t[:3, :3] *= s
    t[:3, 3] = (-(minx + maxx) / 2 * s, -miny * s, -(minz + maxz) / 2 * s)
    scene.apply_transform(t)
    dst.write_bytes(scene.export(file_type="glb"))
    finalize_glb(dst, settings.mesh_unlit if unlit is None else unlit, keep_pbr=keep_pbr)


_gpytoolbox_state: dict = {}


def _has_gpytoolbox() -> bool:
    """SPAR3D's triangle remeshing (lighter models for fillers) needs gpytoolbox."""
    if "ok" not in _gpytoolbox_state:
        try:
            import gpytoolbox  # noqa: F401

            _gpytoolbox_state["ok"] = True
        except ImportError:
            log.warning("gpytoolbox not installed: filler objects keep full models "
                        "(pip install gpytoolbox for lighter ones)")
            _gpytoolbox_state["ok"] = False
    return _gpytoolbox_state["ok"]


class TrellisMeshProvider:
    """TRELLIS.2 (Microsoft, MIT): much cleaner shapes and textures than SPAR3D. Runs as a separate
    worker (scripts/trellis_worker.py) in its own environment; this sends it the cut-out."""

    def generate(self, cutout_png: Path, out_glb: Path, max_faces: int | None = None) -> None:
        import httpx
        import trimesh

        faces = min(settings.trellis_faces, max_faces or settings.trellis_faces)
        t = time.perf_counter()
        try:
            r = httpx.post(settings.trellis_url.rstrip("/") + "/mesh", content=cutout_png.read_bytes(),
                           params={"faces": faces, "texture": settings.trellis_texture,
                                   "type": settings.trellis_type},
                           timeout=settings.mesh_timeout_s)
        except httpx.HTTPError as e:
            raise RuntimeError(f"TRELLIS worker not reachable at {settings.trellis_url} - is it running?") from e
        if r.status_code != 200:
            raise RuntimeError(f"TRELLIS worker error {r.status_code}: {r.text[:300]}")
        log.info("trellis: %d kB model in %.1f s", len(r.content) // 1024, time.perf_counter() - t)
        with tempfile.TemporaryDirectory() as tmp:
            raw = Path(tmp) / "raw.glb"
            raw.write_bytes(r.content)
            if settings.trellis_rotations:
                scene = trimesh.load(raw, force="scene")
                for axis, deg in settings.trellis_rotations:
                    vec = {"x": [1, 0, 0], "y": [0, 1, 0], "z": [0, 0, 1]}[axis]
                    scene.apply_transform(trimesh.transformations.rotation_matrix(np.radians(deg), vec))
                raw.write_bytes(scene.export(file_type="glb"))
            normalize_glb(raw, out_glb, unlit=False, keep_pbr=True)


def trellis_reachable() -> bool:
    import httpx

    try:
        return httpx.get(settings.trellis_url.rstrip("/") + "/health", timeout=2).json().get("ok", False)
    except Exception:  # noqa: BLE001
        return False


def hero_mesh_provider() -> MeshProvider | None:
    """The provider for prominent objects (WHATIF_HERO_MESH_PROVIDER), else the normal one."""
    if settings.hero_mesh_provider == "trellis":
        return TrellisMeshProvider()
    return mesh_provider()


def mesh_provider() -> MeshProvider | None:
    name = settings.mesh_provider
    if name == "trellis":
        return TrellisMeshProvider()
    if name == "none":
        return None
    if name == "crossquad":
        return CrossQuadMeshProvider()
    if name == "command":
        return CommandMeshProvider()
    if name == "triposr":
        return TripoSRMeshProvider()
    if name == "spar3d":
        return Spar3DMeshProvider()
    raise ValueError(f"Unknown WHATIF_MESH_PROVIDER '{name}'")


def image_to_png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()
