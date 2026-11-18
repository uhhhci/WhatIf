"""Moving parts without rigging.

The planner names parts that should move ("tail:wag", "head:nod", "wheels:spin"). Once the cut-out
exists, the vision model (the active planner: local or Claude) says where those parts are in the
image. After the 3D model is built and normalised (1 m tall, base at y=0, image front facing +Z,
image right = +X), every face whose vertices project into a part's box is moved into its own node
"part__<name>__<motion>" with its pivot at the attachment point; the headset animates those nodes.
Parts fused with the body tear a little at the cut - small motions hide it.
"""

from __future__ import annotations

import base64
import io
import logging
import re
from pathlib import Path
from typing import Literal

import numpy as np
from pydantic import BaseModel, Field

log = logging.getLogger("whatif.parts")

MOTIONS = ("wag", "nod", "spin", "flap", "swing")
# what the planner writes instead (seen: tail:sway, head:tilt)
_SYNONYMS = {"sway": "wag", "wiggle": "wag", "swish": "wag", "shake": "wag", "tilt": "nod", "bob": "nod",
             "turn": "nod", "rotate": "spin", "roll": "spin", "flutter": "flap", "beat": "flap", "wave": "swing",
             "walk": "swing", "step": "swing", "kick": "swing", "move": "swing"}


class PartBox(BaseModel):
    name: str
    motion: Literal["wag", "nod", "spin", "flap", "swing"]
    box: list[float] = Field(description="x0, y0, x1, y1 in the image, 0-1 from the top left")
    pivot: list[float] = Field(description="x, y where the part is attached to the body, 0-1 from the top left")


class PartBoxes(BaseModel):
    parts: list[PartBox] = Field(description="Only parts that are clearly visible; empty if none")


_SYSTEM = (
    "You locate parts of an object in a cut-out image (white or transparent background). For each "
    "requested part that is clearly visible, give its bounding box and the point where it is attached "
    "to the body, as fractions of the image (0-1, from the top left). Leave out parts you can't see. "
    "Keep boxes tight around the part only (not the body)."
)


def parse_requests(parts: list[str]) -> list[tuple[str, str]]:
    """'tail:wag' / 'tail wag' -> ('tail', 'wag'); unknown motions dropped, at most 4."""
    out = []
    for p in parts[:4]:
        m = re.match(r"^\s*([\w ]+?)\s*[: ]\s*([a-z]+)\s*$", p.lower())
        if m:
            motion = _SYNONYMS.get(m.group(2), m.group(2))
            if motion in MOTIONS:
                out.append((m.group(1).strip().replace(" ", "_"), motion))
    return out


def locate(cutout_png: Path, requests: list[tuple[str, str]]) -> list[PartBox]:
    """Ask the vision model where the requested parts are in the cut-out."""
    from PIL import Image

    from .planner import PlannerError, structured_call
    from .config import settings

    img = Image.open(cutout_png).convert("RGBA")
    white = Image.new("RGB", img.size, "white")
    white.paste(img, mask=img.split()[3])
    buf = io.BytesIO()
    white.save(buf, format="JPEG", quality=90)
    content = [
        {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg",
                                     "data": base64.b64encode(buf.getvalue()).decode()}},
        {"type": "text", "text": "Locate these parts: " + ", ".join(f"{n} (it will {m})" for n, m in requests)},
    ]
    try:
        found = structured_call(_SYSTEM, content, PartBoxes, settings.edit_effort)
    except PlannerError as e:
        log.warning("parts: %s", e)
        return []
    wanted = dict(requests)
    boxes = []
    for p in found.parts:
        name = p.name.strip().lower().replace(" ", "_")
        if len(p.box) != 4 or len(p.pivot) != 2:
            continue
        x0, y0, x1, y1 = (min(1.0, max(0.0, v)) for v in p.box)
        if x1 - x0 < 0.02 or y1 - y0 < 0.02:
            continue
        p.name, p.motion = name, wanted.get(name, p.motion)
        p.box, p.pivot = [x0, y0, x1, y1], [min(1.0, max(0.0, v)) for v in p.pivot]
        boxes.append(p)
    log.info("parts: %s", ", ".join(f"{p.name}:{p.motion}" for p in boxes) or "none found")
    return boxes


def split(glb: Path, boxes: list[PartBox]) -> int:
    """Move the faces of each part into its own node with the pivot at the attachment point.
    Returns how many parts were split off (the file is rewritten only if any)."""
    import trimesh

    if not boxes:
        return 0
    scene = trimesh.load(glb, force="scene")
    if len(scene.geometry) != 1:
        log.warning("parts: expected one mesh, found %d - not split", len(scene.geometry))
        return 0
    name, mesh = next(iter(scene.geometry.items()))
    mesh = mesh.copy()
    world = scene.graph.get(scene.graph.nodes_geometry[0])[0]
    verts = trimesh.transform_points(mesh.vertices, world)
    (minx, miny, _), (maxx, maxy, _) = verts.min(axis=0), verts.max(axis=0)
    w, h = max(1e-6, maxx - minx), max(1e-6, maxy - miny)
    u = (verts[:, 0] - minx) / w            # image x (front view: image right = +X)
    v = 1.0 - (verts[:, 1] - miny) / h      # image y from the top
    taken = np.zeros(len(mesh.faces), dtype=bool)
    out = trimesh.Scene()
    made = 0
    for p in boxes:
        x0, y0, x1, y1 = p.box
        inside = (u >= x0) & (u <= x1) & (v >= y0) & (v <= y1)
        faces = np.where(inside[mesh.faces].all(axis=1) & ~taken)[0]
        if len(faces) < 20 or len(faces) > 0.6 * len(mesh.faces):
            continue  # too small to matter, or most of the object: not a part
        taken[faces] = True
        part = mesh.submesh([faces], append=True)
        pts = trimesh.transform_points(part.vertices, world)
        pivot = np.array([minx + p.pivot[0] * w, miny + (1.0 - p.pivot[1]) * h, float(pts[:, 2].mean())])
        part.vertices = pts - pivot
        t = np.eye(4); t[:3, 3] = pivot
        out.add_geometry(part, node_name=f"part__{p.name}__{p.motion}", geom_name=f"part_{p.name}", transform=t)
        made += 1
    if not made:
        return 0
    body = mesh.submesh([np.where(~taken)[0]], append=True)
    body.vertices = trimesh.transform_points(body.vertices, world)
    out.add_geometry(body, node_name="body", geom_name=name)
    glb.write_bytes(out.export(file_type="glb"))
    return made


class Facing(BaseModel):
    facing: Literal["left", "right", "front", "back", "unclear"] = Field(
        description="Where the object's front (head, face, nose of a vehicle) points in the image")


def faces_left(cutout) -> bool:
    """Moving things are generated 'side view, facing right'; the image model sometimes draws them
    facing left, and then they swim or drive backwards. True if the vision model sees it facing left."""
    return facing(cutout) == "left"


def facing(cutout) -> str | None:
    """Which way the object's front points in the picture: left, right, front or back (None: unknown)."""
    from PIL import Image

    from .config import settings
    from .planner import PlannerError, structured_call

    img = cutout.convert("RGBA")
    white = Image.new("RGB", img.size, "white")
    white.paste(img, mask=img.split()[3])
    white.thumbnail((512, 512))
    buf = io.BytesIO()
    white.save(buf, format="JPEG", quality=85)
    content = [
        {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg",
                                     "data": base64.b64encode(buf.getvalue()).decode()}},
        {"type": "text", "text": "Which way does this object's front (its head or face, or the front of a "
                                 "vehicle) point in the image: left, right, front (towards the viewer) or back?"},
    ]
    try:
        found = structured_call("You judge the orientation of objects in images. Left and right are as seen "
                                "in the image.", content, Facing, settings.edit_effort)
    except PlannerError as e:
        log.warning("facing: %s", e)
        return None
    log.info("facing: %s", found.facing)
    return found.facing
