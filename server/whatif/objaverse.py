"""Real 3D models from Objaverse as an alternative to generating them.

Objaverse (Allen Institute) is a dataset of ~800k Sketchfab models under Creative Commons licences,
downloadable without a login. scripts/build_objaverse_index.py builds a local search index once:
every usable object (licence, size and face limits) with a one-sentence description (Cap3D) and its
CLIP text embedding. At runtime, for a prominent object:

  1. search: the object's description against all descriptions (CLIP text embeddings, ~0.1 s);
  2. check: the preview pictures of the best candidates against the full description (CLIP image
     match) - a real model is only used when it clearly fits (WHATIF_OBJAVERSE_MIN_SCORE);
  3. use: download, shrink large textures, scale like a generated model, remember the credit
     (CC-BY requires naming the author: /credits lists every model used).

Not used for things that move (their front and moving parts are unknown in foreign models) and not
for fillers (generated ones are fast and match the style). Off unless WHATIF_OBJAVERSE=1.
"""

from __future__ import annotations

import io
import re
import base64
import json
import logging
import threading
import time
from pathlib import Path

import numpy as np
from pydantic import BaseModel, Field
from PIL import Image

from .config import settings

log = logging.getLogger("whatif.objaverse")

HF_BASE = "https://huggingface.co/datasets/allenai/objaverse/resolve/main/"
LICENCES = {"cc0": "CC0 1.0", "by": "CC BY 4.0", "by-sa": "CC BY-SA 4.0"}
_UA = {"User-Agent": "WhatIf/0.1 (https://github.com/uhhhci/WhatIf; AR research prototype)"}


def index_dir() -> Path:
    return settings.data_dir / "objaverse"


class Index:
    """The local search index: one row per usable object (embeddings.npy + entries.jsonl)."""

    def __init__(self, root: Path) -> None:
        self.root = root
        self.embeddings = np.load(root / "embeddings.npy", mmap_mode="r")  # float16, normalised
        with open(root / "entries.jsonl", encoding="utf-8") as f:
            self.entries = [json.loads(line) for line in f]
        if len(self.entries) != len(self.embeddings):
            raise ValueError("objaverse index is inconsistent - rebuild it")

    def search(self, query_embedding: np.ndarray, k: int, allowed: set[str], max_faces: int,
               max_mb: float, min_faces: int = 0, min_likes: int = 0) -> list[tuple[float, dict]]:
        sims = np.asarray(self.embeddings @ query_embedding.astype(np.float16), dtype=np.float32)
        out = []
        for i in np.argsort(-sims)[: k * 40]:  # extra: some fall out by licence, size or quality
            e = self.entries[int(i)]
            liked = "likes" not in e or e.get("staff") or e["likes"] >= min_likes  # old index: no filter
            if e["license"] in allowed and min_faces <= e["faces"] <= max_faces and e["mb"] <= max_mb and liked:
                out.append((float(sims[i]), e))
                if len(out) >= k:
                    break
        return out


_index: Index | None = None
_index_lock = threading.Lock()
_index_failed = False


def index() -> Index | None:
    """Loaded on first use; None (once logged) if it hasn't been built."""
    global _index, _index_failed
    with _index_lock:
        if _index is None and not _index_failed:
            try:
                t = time.perf_counter()
                _index = Index(index_dir())
                log.info("objaverse index: %d objects loaded in %.1f s", len(_index.entries), time.perf_counter() - t)
            except (OSError, ValueError) as e:
                _index_failed = True
                log.warning("objaverse index not available (%s) - run scripts/build_objaverse_index.py", e)
        return _index


def _get(url: str, timeout: float = 20.0) -> bytes:
    import httpx

    r = httpx.get(url, headers=_UA, timeout=timeout, follow_redirects=True)
    r.raise_for_status()
    return r.content


_VIEW_WORDS = re.compile(r",?\s*(side view|facing (right|left)|three-quarter view[^,]*|seen from [^,]*|full body)",
                         re.IGNORECASE)


def _subject(prompt: str) -> str:
    """The description without how the generated picture is framed (a side view, facing right):
    previews show models from other angles, and those words lowered every match."""
    return _VIEW_WORDS.sub("", prompt).strip(" ,")


class _Verdict(BaseModel):
    shows_it: bool = Field(description="True if the picture shows the described thing (the right animal, "
                                       "object or vehicle), not something else")
    realistic: bool = Field(description="True only for a high-quality, realistic, detailed 3D model that would "
                                        "look good from every side, also from the front: not a cartoon or toy "
                                        "unless asked for, not low-poly, not blobby or smeared, not a rough "
                                        "draft or a student exercise")


def confirmed(thumb: Image.Image, subject: str) -> bool | None:
    """The vision model looks at a candidate's preview: is it the described thing, and a good model?
    None if it can't be asked (then the measured scores alone decide)."""
    from .planner import PlannerError, structured_call

    white = Image.new("RGB", thumb.size, "white")
    white.paste(thumb, mask=thumb.split()[3] if thumb.mode == "RGBA" else None)
    white.thumbnail((512, 512))
    buf = io.BytesIO()
    white.save(buf, format="JPEG", quality=85)
    content = [
        {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg",
                                     "data": base64.b64encode(buf.getvalue()).decode()}},
        {"type": "text", "text": f"Described thing: {subject}\nThis is the preview of a downloadable 3D model. "
                                 "Does it show the described thing, and is it a realistic, well-made model?"},
    ]
    try:
        v = structured_call("You check 3D model previews for an AR app before they are shown.", content,
                            _Verdict, settings.edit_effort)
    except PlannerError as e:
        log.warning("objaverse: vision check failed: %s", e)
        return None
    return v.shows_it and v.realistic


def quality(e: dict) -> float:
    """0-1 from Sketchfab's signals: a staff pick is 1, else likes on a log scale (100 likes = 1)."""
    if e.get("staff"):
        return 1.0
    return min(1.0, float(np.log10(1 + e.get("likes", 0))) / 2.0)


def find(prompt: str, query: str | None = None, max_faces: int | None = None,
         picture: Image.Image | None = None, mover: bool = False,
         prominent: bool = True) -> tuple[dict, float] | None:
    """The best real model for this description, or None if none fits clearly enough.
    `query`: a short name to search with (the planner's web search), else the description.
    `max_faces`: a lower limit than usual (fillers).
    `picture`: the cut-out generated for the object: a model whose preview doesn't look like it is not
    used (the text alone let through a cartoon taxi in space for "a yellow taxi"). It also keeps the
    cut-out shown while loading and the final model alike.
    `mover`: an animal or vehicle (side view picture): a lower picture limit; the vision model confirms.
    `prominent`: not a filler - only models with enough Sketchfab likes or a staff pick; better liked
    models rank higher in any case.
    The best candidates are shown to the vision model in turn; the first it confirms is used."""
    from .generators import _clip_scorer

    idx, scorer = index(), _clip_scorer()
    if idx is None or scorer is None:
        return None
    allowed = {x.strip() for x in settings.objaverse_licences.split(",") if x.strip()}
    q = np.asarray(scorer.text_embedding(query or prompt), dtype=np.float32)
    candidates = idx.search(q, settings.objaverse_candidates, allowed, max_faces or settings.objaverse_max_faces,
                            settings.objaverse_max_mb, settings.objaverse_min_faces,
                            settings.objaverse_min_likes if prominent else 0)
    subject = _subject(prompt)
    min_picture = settings.objaverse_min_picture_mover if mover else settings.objaverse_min_picture
    passed: list[tuple[float, dict, Image.Image]] = []
    for text_sim, e in candidates:
        try:
            thumb = Image.open(io.BytesIO(_get(e["thumb"], timeout=8))).convert("RGBA")
        except Exception:  # noqa: BLE001 - a missing preview: skip the candidate
            continue
        match = scorer.score(thumb, subject)
        alike = scorer.picture_similarity(thumb, picture) if picture is not None else None
        # previews are polished renders, so a simple model matches almost as well as a detailed one:
        # among similar matches the more detailed model wins (+0.01 per tenfold triangles); looking like
        # the generated picture counts too
        q_ = quality(e)
        score = (match + 0.01 * float(np.log10(max(e["faces"], 1) / 10000)) + (0.2 * (alike - 0.8) if alike else 0.0)
                 + 0.04 * q_)  # well-liked models first among similar matches
        log.info("objaverse %s: text %.2f, picture %.3f, like the cut-out %s, %d faces, %s likes%s -> %.3f  %s",
                 e["uid"][:8], text_sim, match, f"{alike:.3f}" if alike is not None else "-", e["faces"],
                 e.get("likes", "?"), ", staff pick" if e.get("staff") else "", score, e["name"][:50])
        if alike is not None and alike < min_picture:
            continue  # doesn't look like what was meant (style, material, pose)
        passed.append((score, e, thumb))
    passed.sort(key=lambda x: -x[0])
    # the best few go to the vision model: the scores alone let a meerkat compete with dogs
    for score, e, thumb in passed[:3]:
        verdict = confirmed(thumb, subject)
        if verdict is None:  # no vision model: the scores decide, as before
            if score >= settings.objaverse_min_score:
                return e, score
            break
        log.info("objaverse %s: vision check %s  %s", e["uid"][:8], "yes" if verdict else "no", e["name"][:50])
        if verdict:
            return e, score
    log.info("objaverse: nothing fits %r well enough (%d candidates checked)", subject[:60], min(3, len(passed)))
    return None


class WrongShape(ValueError):
    """The model's proportions don't fit the planned object (seen: a wide tree stump with a disc of
    leaves chosen for a slim 4.5 m jungle trunk - scaled to that height it filled half the room)."""


def shape_fits(width_per_height: float, expected: float | None) -> bool:
    """Horizontal size / height of the model against the plan's footprint / height, with room for
    the planner's rough numbers (a factor 2.5, plus a little for very slim or flat things)."""
    if not expected or expected <= 0:
        return True
    return expected / 2.5 - 0.25 <= width_per_height <= expected * 2.5 + 0.25


class UnknownFront(WrongShape):
    """A model for something that moves, but none of its side views clearly faces one way."""


def render_side(meshes: list, yaw_deg: float, size: int = 384) -> Image.Image:
    """A quick picture of the model turned by `yaw_deg` around the up axis, seen from +Z (so +X is
    to the right): flat-shaded faces in their texture or material colour, drawn back to front. Only
    for judging which way the model faces - not pretty, but the shape and the head show."""
    from PIL import ImageDraw

    a = np.radians(yaw_deg)
    rot = np.array([[np.cos(a), 0, np.sin(a)], [0, 1, 0], [-np.sin(a), 0, np.cos(a)]])
    polys, depths, colours = [], [], []
    for m in meshes:
        v = np.asarray(m.vertices, dtype=np.float64) @ rot.T
        f = np.asarray(m.faces)
        if len(f) > 60000:  # enough for the outline and the head
            f = f[:: int(np.ceil(len(f) / 60000))]
        try:
            vc = np.asarray(m.visual.to_color().vertex_colors, dtype=np.float64)[:, :3]
            col = vc[f].mean(axis=1)
        except Exception:  # noqa: BLE001 - no colours: grey
            col = np.full((len(f), 3), 160.0)
        tri = v[f]
        n = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
        n /= np.linalg.norm(n, axis=1, keepdims=True) + 1e-9
        light = 0.45 + 0.55 * np.abs(n @ np.array([0.3, 0.6, 0.75]))  # some form, sides alike
        polys.append(tri[:, :, :2]); depths.append(tri[:, :, 2].mean(axis=1)); colours.append(col * light[:, None])
    if not polys:
        raise ValueError("no meshes")
    P, D, C = np.concatenate(polys), np.concatenate(depths), np.concatenate(colours)
    lo, hi = P.reshape(-1, 2).min(axis=0), P.reshape(-1, 2).max(axis=0)
    scale = (size * 0.9) / max(hi - lo)
    off = (size - (hi - lo) * scale) / 2
    img = Image.new("RGB", (size, size), "white")
    draw = ImageDraw.Draw(img)
    for i in np.argsort(D):  # far first (camera at +Z)
        pts = [((x - lo[0]) * scale + off[0], size - ((y - lo[1]) * scale + off[1])) for x, y in P[i]]
        c = tuple(int(min(255, max(0, k))) for k in C[i])
        draw.polygon(pts, fill=c)
    return img


def front_yaw(scene) -> float:
    """How far to turn a model so its front points to +X, like generated movers (drawn "side view,
    facing right"): its four side renders are shown to the vision model; the one that faces right
    gives the turn, else one facing left (turned half round). UnknownFront if none clearly does."""
    from .parts import facing

    meshes = [g for g in scene.dump()] if hasattr(scene, "dump") else [scene]
    seen = {}
    for yaw in (0.0, 90.0, 180.0, 270.0):
        seen[yaw] = facing(render_side(meshes, yaw))
        if seen[yaw] == "right":
            log.info("objaverse: front found, turning the model by %.0f deg (views: %s)", yaw, seen)
            return yaw
    for yaw, f in seen.items():
        if f == "left":
            log.info("objaverse: front found, turning the model by %.0f deg (views: %s)", (yaw + 180) % 360, seen)
            return (yaw + 180.0) % 360.0
    raise UnknownFront(f"no side view of the model clearly faces one way ({seen})")


def fetch(entry: dict, out_glb: Path, width_per_height: float | None = None, face_right: bool = False) -> dict:
    """Download the model and fit it like a generated model (1 m tall, base at 0, centred) without
    changing it; returns the credit. `width_per_height`: the planned proportions - a model far off
    them raises WrongShape (a generated model is made instead). `face_right`: for things that move -
    the model is turned so its front points to +X like a generated mover (UnknownFront if that can't
    be found out)."""
    import trimesh

    t = time.perf_counter()
    data = _get(HF_BASE + entry["path"], timeout=settings.mesh_timeout_s)
    # The model stays exactly as its author made it - only measured, then placed under a new top node
    # that scales it to 1 m height, base at y=0, centred. Rewriting it (trimesh) lost transparent leaf
    # cards, double-sided faces and older material types: black trees, a black Eiffel Tower.
    scene = trimesh.load(io.BytesIO(data), file_type="glb", force="scene")
    if _has_skin(data):
        # Rigged models (a skeleton, often an animation) were shown wrongly on the headset: a Santa
        # hundreds of times too big, a dog hovering, an elephant 0.01 m tall without its trunk. They
        # become plain static models in their default pose - the pose measured and checked here - and
        # move as a whole like any other. (Models without a skeleton stay untouched: rewriting them
        # lost transparent leaves and some material types.)
        data = scene.export(file_type="glb")
        log.info("objaverse: %r is rigged - made a static model in its default pose", entry["name"][:50])
    (minx, miny, minz), (maxx, maxy, maxz) = scene.bounds
    height = maxy - miny
    if height <= 0:
        raise ValueError("degenerate model")
    wph = max(maxx - minx, maxz - minz) / height
    if not shape_fits(wph, width_per_height):
        raise WrongShape(f"{entry['name']!r} is {wph:.2f} wide per height, planned {width_per_height:.2f}")
    s = 1.0 / height
    yaw = front_yaw(scene) if face_right else 0.0
    out_glb.write_bytes(wrap_glb(data, s, (-(minx + maxx) / 2 * s, -miny * s, -(minz + maxz) / 2 * s), yaw,
                                 ((minx + maxx) / 2, (minz + maxz) / 2)))
    credit = credit_of(entry)
    log.info("objaverse: %r by %s (%s) in %.1f s", credit["title"], credit["author"], credit["license"],
             time.perf_counter() - t)
    remember(credit)
    return credit


def _standard_materials(gltf: dict) -> None:
    """Older Sketchfab models use specular-glossiness materials: their colour sits in the extension,
    and the headset showed black-and-white leaves (seen: a downloaded tree). They become standard
    metal/roughness materials (same textures). Leaf-like "blend" transparency from a texture becomes a
    cut-out (sharp edges, no sorting problems, works with dimming); glass-like blending stays."""
    ext = "KHR_materials_pbrSpecularGlossiness"
    for m in gltf.get("materials", []):
        sg = (m.get("extensions") or {}).pop(ext, None)
        pbr = m.setdefault("pbrMetallicRoughness", {})
        if sg is not None:
            if "diffuseTexture" in sg:
                pbr["baseColorTexture"] = sg["diffuseTexture"]
            if "diffuseFactor" in sg:
                pbr["baseColorFactor"] = sg["diffuseFactor"]
            pbr["metallicFactor"] = 0.0
            pbr["roughnessFactor"] = max(0.6, 1.0 - float(sg.get("glossinessFactor", 1.0)))
            if not m.get("extensions"):
                m.pop("extensions", None)
        if m.get("alphaMode") == "BLEND" and "baseColorTexture" in pbr:
            m["alphaMode"], m["alphaCutoff"] = "MASK", 0.5
    for key in ("extensionsUsed", "extensionsRequired"):
        if ext in gltf.get(key, []):
            gltf[key] = [x for x in gltf[key] if x != ext]
            if not gltf[key]:
                gltf.pop(key)


def _has_skin(data: bytes) -> bool:
    """A GLB with a skeleton (skins)."""
    import struct

    try:
        n = struct.unpack_from("<I", data, 12)[0]
        return bool(json.loads(data[20:20 + n]).get("skins"))
    except (ValueError, struct.error):
        return False


def needs_refetch(data: bytes) -> bool:
    """A downloaded model from before rigged models were made static or old materials converted."""
    import struct

    try:
        n = struct.unpack_from("<I", data, 12)[0]
        g = json.loads(data[20:20 + n])
    except (ValueError, struct.error):
        return False
    return bool(g.get("skins")) or "KHR_materials_pbrSpecularGlossiness" in g.get("extensionsUsed", [])


def wrap_glb(data: bytes, scale: float, offset: tuple[float, float, float], yaw_deg: float = 0.0,
             centre_xz: tuple[float, float] = (0.0, 0.0)) -> bytes:
    """The same GLB with its scene put under one new node: scale, then move by `offset` - and, with
    `yaw_deg`, turned around the up axis through the model's centre (`centre_xz`, model units). Nothing
    else changes (materials, textures, extensions, animations stay untouched)."""
    import struct

    magic, version, _ = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        raise ValueError("not a GLB file")
    json_len, json_type = struct.unpack_from("<II", data, 12)
    gltf = json.loads(data[20:20 + json_len])
    rest = data[20 + json_len:]  # the binary chunk (and anything after), unchanged
    nodes = gltf.setdefault("nodes", [])
    scenes = gltf.setdefault("scenes", [{"nodes": list(range(len(nodes)))}])
    si = gltf.get("scene", 0)
    roots = scenes[si].get("nodes", [])
    _standard_materials(gltf)
    tx, ty, tz = offset
    if yaw_deg:
        # p' = R * s * (p - centre) + (0, ty, 0): turned around its own middle, then like before
        a = np.radians(yaw_deg)
        c, sn = float(np.cos(a)) * scale, float(np.sin(a)) * scale
        cx, cz = centre_xz
        tx, tz = -(c * cx + sn * cz), -(-sn * cx + c * cz)
        matrix = [c, 0, -sn, 0, 0, scale, 0, 0, sn, 0, c, 0, tx, ty, tz, 1]  # column-major
    else:
        matrix = [scale, 0, 0, 0, 0, scale, 0, 0, 0, 0, scale, 0, tx, ty, tz, 1]
    nodes.append({"name": "whatif_fit", "children": roots, "matrix": matrix})
    scenes[si]["nodes"] = [len(nodes) - 1]
    new_json = json.dumps(gltf, separators=(",", ":")).encode()
    new_json += b" " * (-len(new_json) % 4)
    header = struct.pack("<III", magic, version, 12 + 8 + len(new_json) + len(rest))
    return header + struct.pack("<II", len(new_json), json_type) + new_json + rest


def credit_of(entry: dict) -> dict:
    return {"title": entry["name"], "author": entry["author"], "license": LICENCES.get(entry["license"], entry["license"]),
            "url": f"https://sketchfab.com/3d-models/{entry['uid']}", "uid": entry["uid"]}


_credits_lock = threading.Lock()


def remember(credit: dict) -> None:
    """CC-BY needs the author named: every model used goes into credits.jsonl (shown at /credits)."""
    path = index_dir() / "credits.jsonl"
    with _credits_lock:
        known = {c["uid"] for c in credits()}
        if credit["uid"] not in known:
            path.parent.mkdir(parents=True, exist_ok=True)
            with open(path, "a", encoding="utf-8") as f:
                f.write(json.dumps(credit, ensure_ascii=False) + "\n")


def credits() -> list[dict]:
    path = index_dir() / "credits.jsonl"
    if not path.exists():
        return []
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]
