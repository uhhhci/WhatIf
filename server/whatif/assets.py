"""Asset store and background generation queue.

Assets are content-addressed by (prompt, providers), so asking for "a linden tree"
twice - or from a second headset - reuses the files on disk.
"""

from __future__ import annotations

import hashlib
import json
import logging
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from . import web_images
from .config import settings
from .generators import generate_cutout, gpu, hero_mesh_provider, mesh_provider, warm_up
from .schemas import AssetInfo, AssetStatus

log = logging.getLogger("whatif.assets")


# Bump when generated files change in a way the client can see (e.g. materials, colours),
# so cached assets from older versions are regenerated instead of served.
PIPELINE_VERSION = "8"  # 8: still objects drawn from a three-quarter angle


def _is_patch(prompt: str) -> bool:
    """A coat piece seen from directly above: it lies flat on the floor or a table top."""
    p = prompt.lower()
    return "from directly above" in p or "top-down" in p


def soft_patch(img, seed: int = 0):
    """Gives a top-down cut-out an irregular, soft outline. Such pictures (moss, grass, sand) often
    fill the whole frame: the cut-out stayed a rectangle and the patches on the floor looked like cut
    tiles (seen on the headset). The new outline is a blob - wobbly at three scales - fading out over
    its last fifth, inside what the cut-out already kept."""
    import numpy as np
    from PIL import Image

    rgba = np.asarray(img.convert("RGBA")).astype(np.float32)
    h, w = rgba.shape[:2]
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:h, 0:w]
    dx, dy = (xx - (w - 1) / 2) / (w / 2), (yy - (h - 1) / 2) / (h / 2)
    radius, angle = np.hypot(dx, dy), np.arctan2(dy, dx)
    edge = 0.78 * np.ones_like(angle)
    for k, amp in ((3, 0.10), (5, 0.06), (9, 0.035)):
        edge += amp * np.sin(k * angle + rng.uniform(0, 2 * np.pi))
    # fine ragged noise along the outline, so it frays like grass and moss instead of a smooth curve
    fine = np.asarray(Image.fromarray((rng.random((max(2, h // 12), max(2, w // 12))) * 255).astype(np.uint8))
                      .resize((w, h), Image.BILINEAR)).astype(np.float32) / 255 - 0.5
    t = np.clip((edge + 0.12 * fine - radius) / (0.2 * edge), 0.0, 1.0)  # 1 inside, 0 at the outline
    rgba[..., 3] *= t * t * (3 - 2 * t)
    return Image.fromarray(rgba.astype(np.uint8), "RGBA")


def asset_id(prompt: str, lite: bool = False, parts: tuple = (), web: tuple = (), tiny: bool = False) -> str:
    key = "|".join([prompt.strip().lower(), settings.image_provider, settings.image_model, settings.mesh_provider,
                    str(settings.mesh_texture_size), str(settings.mesh_unlit), PIPELINE_VERSION])
    if tiny:  # a coat piece: lighter still
        key += f"|tiny{settings.mesh_tiny_faces}"
    elif lite:  # a lighter model of the same object is a separate asset
        key += f"|lite{settings.mesh_lite_faces}"
    elif settings.hero_mesh_provider:  # prominent objects from another 3D model: a different asset
        key += f"|hero:{settings.hero_mesh_provider}:{settings.trellis_type}"
    if parts:  # moving parts are split into nodes: a different model
        key += "|parts:" + ",".join(f"{n}:{m}" for n, m in parts)
    if _is_patch(prompt):  # coat patches: cut-outs with a soft irregular outline (not older square ones)
        key += "|patch"
    if "facing right" in prompt.lower():  # movers: cut-outs made since the facing check (not older ones)
        key += "|faced"
    if web and settings.web_images:  # real pictures may win: a different asset
        key += "|web:" + ":".join(web).lower()
    return hashlib.sha1(key.encode()).hexdigest()[:16]


class AssetStore:
    def __init__(self, root: Path, workers: int) -> None:
        self.root = root
        self.root.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._jobs: dict[str, AssetInfo] = {}
        self._inflight: set[str] = set()
        self._images_pending = 0  # cut-outs queued or running: 3D waits for them (no GPU swaps between)
        self._meshes_pending = 0
        self._tried_real: set[str] = set()
        self._name: dict[str, str] = {}  # object's short name (planner's label), for the real-model search  # the download was tried in the picture stage already
        self._lite: set[str] = set()  # asset ids that get a lighter model
        self._tiny: set[str] = set()  # ... and those that get a very light one (coat pieces)
        self._parts: dict[str, list[tuple[str, str]]] = {}  # asset id -> moving parts to split off
        self._web: dict[str, tuple[str, str]] = {}  # asset id -> (search query, thing | person)
        self._shape: dict[str, float] = {}  # asset id -> planned footprint / height
        self._scene: set[str] | None = None  # asset ids of the current scene (None: keep everything)
        # Two queues so every cut-out appears before any slow 3D job: users see all objects
        # within seconds, models upgrade them afterwards. Both fit on a 16 GB GPU together.
        self._pool = ThreadPoolExecutor(max_workers=workers, thread_name_prefix="gen")
        self._mesh_pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="mesh")

    def warm_up(self) -> None:
        """Load the generation models on the worker thread, ahead of the first request."""
        self._pool.submit(warm_up)

    # paths ---------------------------------------------------------------
    def dir(self, aid: str) -> Path:
        return self.root / aid

    def billboard_path(self, aid: str) -> Path:
        return self.dir(aid) / "billboard.png"

    def model_path(self, aid: str) -> Path:
        return self.dir(aid) / "model.glb"

    # public API ----------------------------------------------------------
    def request(self, prompt: str, want_3d: bool, lite: bool = False, reuse: bool = True,
                parts: list[str] | None = None, web: tuple[str, str] | None = None, tiny: bool = False,
                shape: float | None = None, name: str = "") -> AssetInfo:
        """`lite`: a lighter 3D model (fillers: many small things in a full room); `tiny`: lighter still
        (pieces of a coat, placed by the hundred). `reuse`: an object with practically the same
        description may come from the library (not for restyles)."""
        from .parts import parse_requests

        wanted = tuple(parse_requests(parts or [])) if want_3d and settings.moving_parts else ()
        web = (web[0].strip(), web[1]) if web and web[0].strip() else ()
        lite = lite or tiny
        aid = asset_id(prompt, lite, wanted, web, tiny)
        if shape:  # planned footprint / height: a real model of very different proportions is not used
            self._shape[aid] = shape
        if name.strip():  # the object's short name: searches the real-model collection better
            self._name[aid] = name.strip()
        self.add_to_scene(aid)  # edits add to the current scene
        if web and settings.web_images:
            self._web[aid] = web
            reuse = False  # a specific real thing, not "something similar"
        if lite:
            self._lite.add(aid)
        if tiny:
            self._tiny.add(aid)
        if wanted:
            self._parts[aid] = list(wanted)
            reuse = False  # the library holds whole models
        if _is_patch(prompt):
            # lying coat patches are always made fresh: similar ones in the library were cut before the
            # soft outline and lay on the floor as rectangles (seen in a jungle)
            reuse = False
        if (reuse and settings.library and settings.image_provider != "mock"
                and aid not in self._jobs and not self.billboard_path(aid).exists()):
            similar = self._from_library(prompt, lite, want_3d)
            if similar is not None:
                return similar
        with self._lock:
            job = self._jobs.get(aid) or self._from_disk(aid, prompt)
            needs_3d = want_3d and mesh_provider() is not None and job.model_url is None
            self._jobs[aid] = job
            if aid in self._inflight:
                return self._snapshot(job)
            if job.status == AssetStatus.done or (job.status == AssetStatus.billboard_ready and not needs_3d):
                return self._snapshot(job)
            # new, failed (retry) or billboard-only that now needs 3D
            job.status = AssetStatus.queued if job.billboard_url is None else AssetStatus.billboard_ready
            job.error = None
            self._inflight.add(aid)
            self._images_pending += 1
            self._pool.submit(self._run, aid, prompt, want_3d)
            return self._snapshot(job)

    def get(self, aid: str) -> AssetInfo | None:
        with self._lock:
            job = self._jobs.get(aid)
            if job:
                return self._snapshot(job)
        meta = self.dir(aid) / "meta.json"
        if meta.exists():
            return self._from_disk(aid, json.loads(meta.read_text(encoding="utf-8"))["prompt"])
        return None

    # object library ------------------------------------------------------
    def _from_library(self, prompt: str, lite: bool, want_3d: bool) -> AssetInfo | None:
        """A finished object whose description means practically the same (CLIP text similarity)."""
        from .generators import _clip_scorer

        scorer = _clip_scorer()
        if scorer is None:
            return None
        try:
            want = scorer.text_embedding(prompt)
            best, best_aid = 0.0, None
            for aid, (other_lite, emb) in self._library_index(scorer).items():
                if other_lite != lite:
                    continue
                if want_3d and not self.model_path(aid).exists():
                    continue
                sim = sum(a * b for a, b in zip(want, emb))
                if sim > best:
                    best, best_aid = sim, aid
            if best_aid is None or best < settings.library_threshold:
                return None
            meta = json.loads((self.dir(best_aid) / "meta.json").read_text(encoding="utf-8"))
            if meta.get("credit") and self.model_path(best_aid).exists():
                from .objaverse import needs_refetch

                if needs_refetch(self.model_path(best_aid).read_bytes()[:4 << 20]):
                    # downloaded before rigged models / old materials were fixed: shown wrongly - again
                    log.info("library: not reusing %s for %r (rigged or old materials)", best_aid, prompt)
                    return None
            if not lite and settings.objaverse and not meta.get("credit"):
                # a generated prominent object: try the collection of real models first (seen: an old
                # generated coffee cup reused although a good real cup was there); fillers keep reusing
                log.info("library: not reusing generated %s for %r - a real model may fit", best_aid, prompt)
                return None
            from PIL import Image

            from .generators import potted, wild_plant

            if wild_plant(prompt) and potted(scorer, Image.open(self.billboard_path(best_aid)).convert("RGBA")) > 0.5:
                # the library's version stands in a pot (a jungle full of houseplants): make a new one
                log.info("library: not reusing %s for %r - it stands in a pot", best_aid, prompt)
                return None
            log.info("library: reusing %s (%r, similarity %.3f) for %r", best_aid, meta["prompt"], best, prompt)
            with self._lock:
                job = self._jobs.get(best_aid) or self._from_disk(best_aid, meta["prompt"])
                self._jobs[best_aid] = job
                return self._snapshot(job)
        except Exception as e:  # noqa: BLE001 - the library is an optimisation, never a failure
            log.warning("library lookup failed: %s", e)
            return None

    def _library_index(self, scorer) -> dict[str, tuple[bool, list[float]]]:
        """Embeddings of all finished objects on disk, computed once and kept up to date."""
        if not hasattr(self, "_index"):
            self._index: dict[str, tuple[bool, list[float]]] = {}
        for meta in self.root.glob("*/meta.json"):
            aid = meta.parent.name
            if aid in self._index or not self.billboard_path(aid).exists():
                continue
            try:
                info = json.loads(meta.read_text(encoding="utf-8"))
                if info.get("pipeline") != PIPELINE_VERSION:
                    continue  # made by an older pipeline (e.g. flat side views): not reused
                self._index[aid] = (bool(info.get("lite", False)), scorer.text_embedding(info["prompt"]))
            except (OSError, ValueError, KeyError):
                continue
        return self._index

    # internals -----------------------------------------------------------
    def _snapshot(self, job: AssetInfo) -> AssetInfo:
        return job.model_copy(update={"pending": job.id in self._inflight})

    def _from_disk(self, aid: str, prompt: str) -> AssetInfo:
        info = AssetInfo(id=aid, prompt=prompt, status=AssetStatus.queued)
        meta = self.dir(aid) / "meta.json"
        if self.billboard_path(aid).exists() and meta.exists():
            info.billboard_url = f"/assets/{aid}/billboard.png"
            info.billboard_aspect = json.loads(meta.read_text(encoding="utf-8")).get("aspect")
            info.status = AssetStatus.billboard_ready
        if self.model_path(aid).exists():
            info.model_url = f"/assets/{aid}/model.glb"
            info.status = AssetStatus.done
        return info

    def _update(self, aid: str, **changes) -> None:
        with self._lock:
            job = self._jobs[aid]
            for k, v in changes.items():
                setattr(job, k, v)

    def _run(self, aid: str, prompt: str, want_3d: bool) -> None:
        """Cut-out stage (image queue). Hands over to the 3D queue if a model is wanted."""
        d = self.dir(aid)
        d.mkdir(parents=True, exist_ok=True)
        try:
            if not self.billboard_path(aid).exists():
                self._update(aid, status=AssetStatus.running)
                log.info("image  %s  %r", aid, prompt)
                pictures = web_images.find(*self._web[aid]) if aid in self._web else []
                cutout, tries = generate_cutout(prompt, pictures)
                if "facing right" in prompt.lower() and settings.planner != "mock":
                    from PIL import ImageOps

                    from .parts import faces_left

                    if faces_left(cutout):  # it would move backwards: mirror it
                        cutout = ImageOps.mirror(cutout)
                        log.info("cut-out %s mirrored to face right", aid)
                if _is_patch(prompt):  # a coat piece lying flat: no straight picture edges
                    cutout = soft_patch(cutout, seed=int(aid[:8], 16))
                log.info("cut-out %s  after %d attempt(s)", aid, tries)
                cutout.save(self.billboard_path(aid))
                aspect = cutout.width / cutout.height
                (d / "meta.json").write_text(json.dumps({"prompt": prompt, "aspect": aspect, "lite": aid in self._lite,
                                                         "pipeline": PIPELINE_VERSION}),
                                             encoding="utf-8")
                self._update(aid, status=AssetStatus.billboard_ready,
                             billboard_url=f"/assets/{aid}/billboard.png", billboard_aspect=aspect)
            if want_3d and mesh_provider() is not None:
                if not self.model_path(aid).exists():
                    # a real model right here, a second after the picture: in the 3D queue it waited behind
                    # every slow generation queued before it
                    real = self.model_path(aid).with_suffix(".real.glb")
                    if self._real_model(aid, real):
                        real.replace(self.model_path(aid))
                    self._tried_real.add(aid)
                if self.model_path(aid).exists():
                    self._update(aid, status=AssetStatus.done, model_url=f"/assets/{aid}/model.glb")
                else:
                    with self._lock:
                        self._meshes_pending += 1
                    self._mesh_pool.submit(self._run_mesh, aid)
                    return  # still in flight
            else:
                self._update(aid, status=AssetStatus.billboard_ready)
        except Exception as e:  # noqa: BLE001 - report every failure to the client
            self._fail(aid, e)
        finally:
            with self._lock:
                self._images_pending -= 1
        self._finish(aid)

    def new_scene(self, aids: set[str]) -> None:
        """A new scene replaced the old one: 3D models still queued for objects that are no longer
        shown are skipped (they kept the new scene's models waiting for minutes). They keep their
        cut-out and get the model when they are asked for again."""
        with self._lock:
            self._scene = set(aids)

    def add_to_scene(self, aid: str) -> None:
        with self._lock:
            if self._scene is not None:
                self._scene.add(aid)

    def _run_mesh(self, aid: str) -> None:
        """3D stage (mesh queue). Waits until all queued cut-outs are done, so the GPU only
        switches between image models and a large 3D model once per batch."""
        with self._lock:
            stale = self._scene is not None and aid not in self._scene and not self.model_path(aid).exists()
        if stale:
            log.info("mesh   %s skipped: no longer in the scene", aid)
            with self._lock:
                self._meshes_pending -= 1
                job = self._jobs.get(aid)
                if job is not None:
                    job.status = AssetStatus.billboard_ready
            self._finish(aid)
            return
        while gpu.swaps:  # with everything on the GPU, 3D starts right away
            with self._lock:
                if self._images_pending == 0:
                    break
            time.sleep(0.1)
        try:
            if not self.model_path(aid).exists():
                log.info("mesh   %s", aid)
                tmp = self.model_path(aid).with_suffix(".tmp.glb")
                faces = (settings.mesh_tiny_faces if aid in self._tiny else
                         settings.mesh_lite_faces if aid in self._lite else None)
                # a real model that clearly fits (Objaverse) instead of a generated one; else generate -
                # prominent objects may use a stronger model (WHATIF_HERO_MESH_PROVIDER), fillers not
                if aid in self._tried_real or not self._real_model(aid, tmp):
                    provider = mesh_provider() if aid in self._lite else hero_mesh_provider()
                    provider.generate(self.billboard_path(aid), tmp, max_faces=faces)
                    if self._parts.get(aid):
                        self._split_parts(aid, tmp)  # generated models only: a real one moves as a whole
                tmp.replace(self.model_path(aid))
            self._update(aid, status=AssetStatus.done, model_url=f"/assets/{aid}/model.glb")
        except Exception as e:  # noqa: BLE001
            self._fail(aid, e)
        finally:
            with self._lock:
                self._meshes_pending -= 1
                idle = self._meshes_pending == 0
            if idle and gpu.active not in (None, "image"):
                # No more 3D work: bring the image models back now, so the next request starts fast.
                self._pool.submit(self._swap_back)
        self._finish(aid)

    def _real_model(self, aid: str, out: Path) -> bool:
        """Objaverse: only prominent, still objects (no fillers, nothing that moves or has moving parts:
        their front and parts are unknown in foreign models). Any failure falls back to generating."""
        # fillers too (seen: generated desert rocks looked like bubbly pastry; scanned rocks are real),
        # but no coat pieces (flat cut-outs) and with a lower triangle limit
        # things that move too (seen: a generated cat with wrong anatomy): turned so their front points
        # like a generated mover's; their moving parts (tail, legs) are not cut out of a real model -
        # it moves as a whole
        if not settings.objaverse or settings.image_provider == "mock" or aid in self._tiny:
            return False
        job = self._jobs.get(aid)
        prompt = job.prompt if job else ""
        if not prompt:
            return False
        mover = "facing right" in prompt.lower()
        from . import objaverse

        try:
            from PIL import Image

            picture = None
            if self.billboard_path(aid).exists():
                picture = Image.open(self.billboard_path(aid)).convert("RGBA")
            query = self._web[aid][0] if aid in self._web else self._name.get(aid)
            found = objaverse.find(prompt, query=query,
                                   max_faces=settings.objaverse_filler_max_faces if aid in self._lite else None,
                                   picture=picture, mover=mover, prominent=aid not in self._lite)
            if found is None:
                return False
            entry, score = found
            credit = objaverse.fetch(entry, out, self._shape.get(aid), face_right=mover)
        except objaverse.WrongShape as e:
            log.info("objaverse for %s not used: %s", aid, e)
            return False
        except Exception as e:  # noqa: BLE001 - a generated model is still a model
            log.warning("objaverse for %s failed: %s", aid, e)
            return False
        meta = self.dir(aid) / "meta.json"
        try:
            info = json.loads(meta.read_text(encoding="utf-8")) if meta.exists() else {}
            info["credit"] = credit
            info["credit_score"] = round(score, 3)
            meta.write_text(json.dumps(info), encoding="utf-8")
        except (OSError, ValueError):
            pass
        return True

    def _split_parts(self, aid: str, glb: Path) -> None:
        """Moving parts: locate them in the cut-out, cut them out of the model as separate nodes."""
        from .generators import finalize_glb
        from .parts import locate, split

        try:
            boxes = locate(self.billboard_path(aid), self._parts[aid])
            if split(glb, boxes):
                spar3d = settings.mesh_provider == "spar3d"
                finalize_glb(glb, False if spar3d else settings.mesh_unlit, keep_pbr=spar3d)
        except Exception as e:  # noqa: BLE001 - a model without moving parts is still a model
            log.warning("parts for %s failed: %s", aid, e)

    def _swap_back(self) -> None:
        with self._lock:
            if self._meshes_pending:
                return
        with gpu.use("image"):
            pass

    def _fail(self, aid: str, e: Exception) -> None:
        log.exception("generation failed for %s", aid)
        with self._lock:
            job = self._jobs[aid]
            # A failed 3D step still leaves a usable billboard.
            job.status = AssetStatus.billboard_ready if job.billboard_url else AssetStatus.failed
            job.error = f"{type(e).__name__}: {e}"

    def _finish(self, aid: str) -> None:
        with self._lock:
            self._inflight.discard(aid)


store = AssetStore(settings.data_dir / "assets", settings.workers)
