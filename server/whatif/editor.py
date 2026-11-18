"""Turns a spoken edit ("make it bigger", "move the chairs to the other side of the room",
"make the baby penguins blue", "delete all trees", "add a lamp next to the sofa") into a list of
generic operations on the headset's current scene.

Nothing is hard-coded per command: Claude gets the object list (ids, kinds, descriptions, where
they are relative to the user), what the user looked at, what changed last and the detected
surfaces, and answers with operations from a small fixed set. The headset applies them.
"""

from __future__ import annotations

import logging
import re

from . import assets as _assets
from . import config as _config
from .planner import (
    PlannerError,
    _COLOUR_WORDS,
    _enforce_surfaces,
    _moves,
    _sanitize,
    available_surfaces,
    structured_call,
    clean_behaviour,
    clean_hex,
    mark_image,
    named_surface,
    normalise_effect,
    parse_image_point,
    resolve_surfaces,
    short_ids,
)
from .schemas import (
    AppliedOp,
    BehaviourChange,
    AssetItem,
    EditOp,
    EditOpType,
    EditPlan,
    EditRequest,
    EditResponse,
    Facing,
    Layout,
    Placement,
    PlanRequest,
    ScenePlan,
    Surface,
)

log = logging.getLogger("whatif.editor")

# Words that ask for a colour on real surfaces (or for lighter / darker / no paint): without one,
# a paint operation is a guess at a mood and only shows as flat plates
_PAINT_WISH = re.compile(_COLOUR_WORDS.pattern + r"|dark|light|hell|dunkel|paint|remove|entfern", re.IGNORECASE)

SYSTEM_PROMPT = """\
You edit the scene of "What if?", an augmented-reality app on a Magic Leap 2 headset. The user
created a scenario ("What if this place had more plants?") and now changes it by voice. You get
what they said, the virtual objects currently in the scene and the real surfaces the headset
detected. Answer with operations; the headset applies them.

Which objects are meant:
- "it", "this", "that", "this one": the object the user looked at while speaking (marked LOOKED AT).
  If none is marked, the most recently changed objects.
- Kinds and descriptions: "all chairs", "the trees", "the baby penguins" (vs. the adult ones), "the
  blue one": match against label and description; plural = all matching objects.
- Positions: "the left one" = most negative bearing, "the one behind me" = |bearing| > 120,
  "the closest" = smallest distance.
- targets must be object ids from the list (e.g. "chair#2"), never invented ones.

Operations (combine several if needed, in order):
- move: shift by metres in the user's view: dx_right_m (- = left), dy_up_m, dz_forward_m
  (+ = away from the user, - = closer). "a bit" ~ 0.3 m, "to the left" ~ 1 m.
- move_to: either bearing_deg + distance_m from the user ("to the other side of the room": the
  opposite direction, far side of the floor; "next to me": distance ~1 m), or target_id + relation
  (on / beside / around / under) for objects or surfaces ("put the vase on the table", "the chairs
  around the dining table", "the cat under the table" - under shrinks it to fit below). For a real thing visible in the camera image that is neither an object nor
  a surface (a keyboard, a person's head, a bin): target_id "IMG x,y", the point in the image
  (0-1 from the top left, tick marks on the edges); relation on = on top of it, beside = next to
  it on the floor. "There" / "da" / "dort" (where the user looked while speaking, given as
  "looked at"): target_id "GAZE" with relation on. A thing the user NAMES comes first ("on the
  table", "auf den Schrank"): its S-number if detected, else its point in the image (IMG x,y) -
  GAZE only when the place isn't named. Also as an added item's placement target_id ("put a crown on Tim's head":
  add a crown, layout single, target_id the top of his head in the image).
- scale: scale_factor ("bigger" ~ 1.5, "much bigger" ~ 2, "smaller" ~ 0.66, "twice as big" = 2).
- rotate: rotate_deg (clockwise seen from above; "turn around" = 180).
- tip: a different pose of the SAME object by laying it down: rotate_deg 180 = on its back /
  upside down, 90 = on its side / fallen over, -90 = tipped forward. Never restyle for these.
- face: facing toward_user, or toward_target with target_id.
- restyle: same objects, different look: asset_prompt is the full new English description of ONE
  object, keeping what wasn't asked to change ("make it blue": "a blue wooden dining chair,
  realistic photo"); label is the new short name. Also for "make it a palm tree" and for things
  it wears or holds ("give it a hat"). Put every changed or unusual feature FIRST, in plain
  words, before the object's usual looks: the image model weights the first words most and
  otherwise draws the ordinary version ("a bright blue giraffe wearing a big red top hat, full
  body, side view", not "a giraffe ... with blue fur ... wearing a hat"). Keep earlier changes
  (a blue giraffe that gets a hat stays blue). Always the whole object ("full body") - never a
  close-up or a head.
- delete.
- add: new objects as a plan item (asset_prompt in English, at most 25 words, one isolated SOLID
  3D object - never a flat poster, sign, wall panel, wall texture or rug;
  realistic height_m and footprint_m; placement with surface, layout, facing, count, and target_id
  + layout around/beside to relate them to an object or surface). Keep item ids unique.
- paint: colour real surfaces ("paint the walls green", "make the floor darker", "remove the
  paint"): targets are surface S-numbers or kinds (wall, floor, ceiling, table), color_hex the
  colour ("none" removes the paint). "Darker" / "lighter" adjust the current paint colour.
  A virtual OBJECT in a new colour ("paint the chair red") is a restyle, not paint.
- undo: revert the previous edit ("undo", "go back", "rückgängig").

Effects (surface looks, levels, tint) are changed with `effects`: the complete new list, built
from the same blocks as in the scene (colours, pattern, motion, glow, reflect, edge, particles):
"make the lava glow more" raises glow, "water higher" raises water_height_m, "add fog" adds a
level, "no more water" leaves it out. A plain new colour of a real surface stays a paint operation.

How objects behave is changed with `behaviours`: the objects and their complete new behaviour
(same building blocks as in the plan): "let the dog sit" (one state, idle breathe), "the car
should drive faster" (higher speed), "make the bird circle the lamp" (move to_target / circle).

Removing everything ("delete the whole scene", "clear everything", "remove it all", "alles weg",
"lösch die ganze Szene") is ONE operation of type `clear` - not deletes of single objects (big
scenes list only some objects by name, deletes would leave the rest).

How much changes is decided by WHAT the sentence asks for, never by how it starts: "What if ..."
/ "Was wäre, wenn ..." is also just a way of asking for a change.
- A whole different place or theme replacing the current one ("what if this were a beach", "now
  make it a jungle", "turn this into a castle", "Mach hier einen Strand draus"): not an edit - put
  it into `new_scene` as a "What if ..." sentence in the user's language and return no operations.
- Anything that fits into the current scene stays an edit and keeps everything else: adding things
  ("what if there was a coffee mug on my table" - add one mug on the table; "what if a cat slept
  under the desk"; "add some palm trees"), changing or removing objects, changing effects ("what if
  it were night", "what if the water rose").
When unsure, keep the scene and edit it.

If the sentence is not meant for the scene (talking to someone else, thinking aloud, noise), return
no operations and an empty reply. Otherwise reply with one short sentence in the user's language
(English -> English, German -> German) saying what you did.
"""


def _side(bearing: float) -> str:
    if abs(bearing) < 10:
        return "straight ahead"
    if abs(bearing) > 135:
        return "behind the user"
    return f"{abs(bearing):.0f} deg to the {'right' if bearing > 0 else 'left'}"


def _look(e) -> str:
    """An effect's blocks in a few words, so edits can change them ("make the lava glow more")."""
    parts = [f"colour {e.tint_hex}", f"opacity {e.strength:.2f}"]
    if e.colors:
        parts.append("colours " + "/".join(e.colors))
    if e.pattern != "none":
        parts.append(f"pattern {e.pattern} {e.pattern_scale_m:g} m")
    if e.motion != "still":
        parts.append(f"motion {e.motion} speed {e.speed:g}")
    if e.glow > 0:
        parts.append(f"glow {e.glow:.2f} {e.glow_hex or ''}".rstrip())
    if e.reflect > 0:
        parts.append(f"reflect {e.reflect:.2f}")
    if e.edge != "none":
        parts.append(f"edge {e.edge}")
    if e.particles != "none":
        parts.append(f"particles {e.particles} {e.particle_amount:.2f}")
    return ", ".join(parts)


def _object_lines(req: EditRequest, max_chars: int = 12000) -> list[str]:
    """The scene's objects by group: each group once (label, description, number of copies), and by
    name only the copies that matter - looked at, just changed, the nearest. One line per copy ran a
    full jungle with coats (hundreds of copies) past the local model's context (seen: 16385 of
    16384 tokens for "Tschüssi, bis Mittwoch"). Fewer copies by name if it is still too long."""
    groups: dict[str, list] = {}
    for o in req.objects:
        groups.setdefault(o.group, []).append(o)
    recent = set(req.last_ids)

    def looked(o) -> bool:
        return o.looked_at or o.id == req.gaze_id

    for per in (6, 3, 1):
        lines = ["Objects in the scene, by group (a whole group is targeted by its group id, single copies "
                 "by their id):"]
        for g, objs in groups.items():
            first = objs[0]
            n = len(objs)
            lines.append(f"- group {g}: {first.label}, \"{first.asset_prompt}\", {n} cop{'y' if n == 1 else 'ies'}, "
                         f"footprint {first.footprint_m:.2f} m")
            shown = sorted(objs, key=lambda o: (not looked(o), o.id not in recent, o.distance_m))[:per]
            for o in shown:
                lines.append(
                    f"  - {o.id}: {o.distance_m:.1f} m away, {_side(o.bearing_deg)}, {o.height_m:.2f} m tall"
                    + (f", standing {o.height_above_floor_m:.2f} m above the floor" if o.height_above_floor_m > 0.1 else "")
                    + ("  <- LOOKED AT" if looked(o) else ""))
            if n > len(shown):
                lines.append(f"  - and {n - len(shown)} more")
        if sum(len(x) + 1 for x in lines) <= max_chars:
            break
    return lines


def _scene_text(req: EditRequest) -> str:
    lines = [f'The user said: "{req.utterance}"', ""]
    if req.english_only:
        lines += ["Write `reply` and `new_scene` in English, whatever language the user speaks.", ""]
    if req.natural:
        lines += ["The user was not looking at the generated objects while saying this: most likely it is "
                  "talk to someone else. Return no operations unless it is clearly an instruction to the "
                  "app to add, change or remove something ('make the dog bigger', 'remove the trees', "
                  "'häng ein Bild an die Wand'). Comments on what is visible, exclamations and descriptions "
                  "('what a giant alien', 'das ist Indiana Jones'), questions to people, greetings and "
                  "goodbyes ('Tschüss, bis Mittwoch') are talk: no operations.", ""]
    if req.looked_at:
        lines += [f"While speaking, the user looked at: {req.looked_at} (target_id GAZE).", ""]
    if req.scenario:
        lines.append(f"Current scenario: {req.scenario}")
    if req.objects:
        lines += _object_lines(req)
    else:
        lines.append("The scene has no objects yet.")
    if req.last_ids:
        lines.append("Most recently changed: " + ", ".join(req.last_ids))
    plan_req = PlanRequest(prompt=req.utterance, scene=req.scene, english_only=req.english_only)
    sid_of = dict(zip((p.id for p in req.scene.planes), short_ids(plan_req)))
    for e in req.effects:
        look = _look(e)
        if e.type.value in ("surface", "surface_paint"):
            where = ", ".join(sid_of.get(s, s) for s in e.surfaces)
            lines.append(f"Surface effect on {where}: {look}")
        elif e.type.value in ("level", "water_level"):
            lines.append(f"Level effect {e.water_height_m or 0:.2f} m above the floor: {look}")
        else:
            lines.append(f"Effect: tint {e.tint_hex} (strength {e.strength:.2f})")
    if req.scene.planes:
        lines.append("Detected surfaces:")
        for sid, p in zip(short_ids(plan_req), req.scene.planes):
            bearing = f", {_side(p.bearing_deg)}" if p.bearing_deg is not None else ""
            lines.append(f"- {sid}: {p.label}, {p.width_m:.1f} x {p.height_m:.1f} m, {p.distance_m:.1f} m away{bearing}")
    lines.append("You can place objects on: " + ", ".join(sorted(available_surfaces(plan_req))) + ".")
    return "\n".join(lines)


def edit_scene(req: EditRequest) -> EditResponse:
    s = _config.settings
    if req.natural and (s.planner == "mock" or (s.planner == "claude" and not s.natural_requests_online)):
        return EditResponse(reply="", operations=[])  # such sentences never leave the server
    if s.planner == "mock":
        plan = mock_edit(req)
    else:
        content: list[dict] = []
        if req.image_jpeg_b64:
            plan_req = PlanRequest(prompt=req.utterance, scene=req.scene, english_only=req.english_only)
            image = mark_image(req.image_jpeg_b64, list(zip(short_ids(plan_req), req.scene.planes)))
            content.append({"type": "image", "source": {"type": "base64", "media_type": "image/jpeg", "data": image}})
        content.append({"type": "text", "text": _scene_text(req)})
        plan = structured_call(SYSTEM_PROMPT, content, EditPlan, _config.settings.edit_effort)
    return _apply_server_side(plan, req)


# --------------------------------------------------------------------------- checks + generation


def _apply_server_side(plan: EditPlan, req: EditRequest) -> EditResponse:
    """Keep the operations to objects that exist, expand groups, map S-numbers to the headset's
    plane ids, and start generating new looks / new objects right away."""
    by_id = {o.id: o for o in req.objects}
    by_group: dict[str, list[str]] = {}
    for o in req.objects:
        by_group.setdefault(o.group, []).append(o.id)
    plane_ids = dict(zip(short_ids(PlanRequest(prompt="x", scene=req.scene)), (p.id for p in req.scene.planes)))
    default_targets = [req.gaze_id] if req.gaze_id in by_id else [i for i in req.last_ids if i in by_id]
    groups_taken = set(by_group)

    def resolve_target(t: str | None) -> str | None:
        if not t:
            return None
        if t.upper() in ("GAZE", "THERE") and (named := named_surface(req.utterance, req)):
            return named  # "on the table" without "there": the named table, not the gaze point
        if t.upper() in ("GAZE", "THERE") and req.looked_at:
            return "gaze"  # the point the user looked at while speaking
        point = parse_image_point(t) if req.image_jpeg_b64 else None
        if point:
            return point  # a real thing in the camera image
        if t.upper() in plane_ids:
            return plane_ids[t.upper()]
        return t if t in by_id or t in by_group else None

    applied: list[AppliedOp] = []
    plan_req = PlanRequest(prompt=req.utterance, scene=req.scene, english_only=req.english_only)
    for op in plan.operations:
        if op.type == EditOpType.paint:
            if not _PAINT_WISH.search(req.utterance):
                # seen: "what if it were night" painted floor, table and walls dark blue on top of the
                # night light - flat dark plates; light and mood are effects
                log.info("dropping paint %s on %s: the sentence asks for no colour", op.color_hex, op.targets)
                continue
            surfaces = resolve_surfaces(op.targets or ["wall"], plan_req)
            colour = "none" if op.color_hex.strip().lower() == "none" else clean_hex(op.color_hex)
            if surfaces and colour:
                out = AppliedOp(**op.model_dump(), asset=None)
                out.targets, out.color_hex = surfaces, colour
                applied.append(out)
            continue
        targets: list[str] = []
        for t in op.targets:
            targets += by_group.get(t, [t] if t in by_id else [])
        targets = list(dict.fromkeys(targets))  # unique, in order
        if op.type == EditOpType.clear:
            return EditResponse(reply=plan.reply, operations=[AppliedOp(**op.model_dump(), asset=None)])
        needs_targets = op.type not in (EditOpType.add, EditOpType.undo)
        if needs_targets and not targets:
            targets = default_targets
        if needs_targets and not targets:
            continue  # nothing to apply it to
        out = AppliedOp(**op.model_dump(), asset=None)
        out.targets = targets
        out.target_id = resolve_target(op.target_id) or ""

        if op.type == EditOpType.scale:
            out.scale_factor = max(0.1, min(10.0, op.scale_factor or 1.0))
        if op.type == EditOpType.move_to and out.distance_m <= 0 and not out.target_id:
            continue  # nowhere to move to
        if op.type == EditOpType.restyle:
            if not op.asset_prompt:
                continue
            # a restyle asks for a new look: never from the library
            out.asset = _assets.store.request(op.asset_prompt, True, reuse=False) if req.generate else None
        if op.type == EditOpType.add and op.item is not None and op.relation in ("under", "beside", "around"):
            # the relation wins over the item's own layout (seen: "a cat under the table" with layout
            # single on the table - it would have slept on top of it)
            p = op.item.placement
            p.layout, p.surface = Layout(op.relation), Surface.floor
            p.target_id = p.target_id or op.target_id
        if op.type == EditOpType.add:
            item = _checked_item(op.item, req, groups_taken, resolve_target)
            if item is None:
                continue
            groups_taken.add(item.id)
            out.item = item
            out.asset = (_assets.store.request(item.asset_prompt, item.want_3d, lite=item.role == "filler", name=item.label,
                                               parts=item.parts, web=(item.web_search, item.web_kind),
                                               tiny=item.placement.layout in (Layout.coat, Layout.drape),
                                               shape=item.placement.footprint_m / max(0.05, item.placement.height_m))
                         if req.generate else None)
        applied.append(out)
    new_effects = None
    if plan.effects is not None:
        new_effects = []
        for e in plan.effects:
            e = normalise_effect(e)
            if e.type.value == "surface":
                e.surfaces = resolve_surfaces(e.surfaces or ["floor"], plan_req)
                if not e.surfaces:
                    continue
            new_effects.append(e)
    new_scene = (plan.new_scene or "").strip()
    if new_scene:  # a whole new scene: the headset starts it like a "What if" prompt
        return EditResponse(reply="", operations=[], new_scene=new_scene[:500])
    changes = []
    for ch in plan.behaviours:
        ids: list[str] = []
        for t in ch.targets:
            ids += by_group.get(t, [t] if t in by_id else [])
        b = clean_behaviour(ch.behaviour)
        if ids and b is not None:
            ids = list(dict.fromkeys(ids))
            changes.append(BehaviourChange(targets=ids, behaviour=b))
            if _moves(b):
                applied += _side_view_restyles(ids, req)
    return EditResponse(reply=plan.reply, operations=applied, effects=new_effects, behaviours=changes)


def _side_view_restyles(ids: list[str], req: EditRequest) -> list[AppliedOp]:
    """Objects that start moving need a known front. Ones whose model was not made "side view, facing
    right" (added while standing still) are made again that way (seen: birds set flying later flew
    backwards). One restyle per kind of object."""
    by_prompt: dict[str, list[str]] = {}
    for o in req.objects:
        if o.id in ids and o.asset_prompt and "facing right" not in o.asset_prompt.lower():
            by_prompt.setdefault(o.asset_prompt, []).append(o.id)
    out = []
    for prompt, targets in by_prompt.items():
        new = (prompt.rstrip(". ") + ", side view, facing right")[:300]
        op = AppliedOp(**EditOp.make(EditOpType.restyle, targets=targets, asset_prompt=new).model_dump(), asset=None)
        op.asset = _assets.store.request(new, True, reuse=False) if req.generate else None
        log.info("restyle %s: moving now, made again in side view", targets)
        out.append(op)
    return out


def _checked_item(item: AssetItem | None, req: EditRequest, taken: set[str], resolve) -> AssetItem | None:
    if item is None:
        return None
    plan_req = PlanRequest(prompt=req.utterance, scene=req.scene, english_only=req.english_only)
    plan = _enforce_surfaces(_sanitize(ScenePlan(interpretation="", realism="realistic", items=[item],
                                                 effects=[], note_for_user="")), plan_req)
    if not plan.items:
        return None
    item = plan.items[0]
    base, n = item.id, 2
    while item.id in taken:
        item.id, n = f"{base}_{n}", n + 1
    p = item.placement
    p.target_id = resolve(p.target_id)
    if p.target_id is None and p.surface == Surface.table and (named := named_surface(req.utterance, req)):
        p.target_id = named  # "a cola bottle on the table" without a target: that table, not a seat
    if p.layout in (Layout.around, Layout.beside, Layout.under) and p.target_id is None:
        p.layout = Layout.scatter
    if p.facing == Facing.toward_target and p.target_id is None:
        p.facing = Facing.toward_user
    return item


# --------------------------------------------------------------------------- offline mock


def mock_edit(req: EditRequest) -> EditPlan:
    """Keyword rules, only to test the pipeline without Claude."""
    u = req.utterance.lower()
    targets = [o.id for o in req.objects if any(w in u for w in (o.label.lower(), o.group.lower()))]
    ops: list[EditOp] = []
    if re.search(r"undo|rückgängig|go back", u):
        ops.append(EditOp.make(EditOpType.undo))
    elif re.search(r"delete|remove|lösch|entfern", u):
        ops.append(EditOp.make(EditOpType.delete, targets=targets))
    elif re.search(r"bigger|larger|größer", u):
        ops.append(EditOp.make(EditOpType.scale, targets=targets, scale_factor=1.5))
    elif re.search(r"smaller|kleiner", u):
        ops.append(EditOp.make(EditOpType.scale, targets=targets, scale_factor=0.66))
    elif re.search(r"\bleft\b|links", u):
        ops.append(EditOp.make(EditOpType.move, targets=targets, dx_right_m=-1.0))
    elif re.search(r"\bright\b|rechts", u):
        ops.append(EditOp.make(EditOpType.move, targets=targets, dx_right_m=1.0))
    elif m := re.search(r"\b(?:paint|streich)\w* (?:the |die )?(walls?|wände|wand|floor|boden)\b.*?\b(blue|red|green|blau|rot|grün)", u):
        hexes = {"blue": "#3050c0", "blau": "#3050c0", "red": "#b03030", "rot": "#b03030", "green": "#3a8a40", "grün": "#3a8a40"}
        ops.append(EditOp.make(EditOpType.paint, targets=["floor" if m.group(1) in ("floor", "boden") else "wall"],
                               color_hex=hexes[m.group(2)]))
    elif m := re.search(r"\b(blue|red|green|yellow|blau|rot|grün|gelb)\b", u):
        ops.append(EditOp.make(EditOpType.restyle, targets=targets,
                          asset_prompt=f"a {m.group(1)} object, realistic photo", label=f"{m.group(1)} object"))
    elif m := re.search(r"\badd (?:an? )?([a-z ]+)", u):
        what = m.group(1).strip()
        ops.append(EditOp.make(EditOpType.add, item=AssetItem(
            id=what.replace(" ", "_"), label=what, asset_prompt=f"a {what}, realistic photo", want_3d=True,
            placement=Placement(surface=Surface.floor, layout=Layout.single, facing=Facing.toward_user, count=1,
                                height_m=1.0, footprint_m=0.8, height_jitter=0.1, min_spacing_m=1.0,
                                max_distance_m=5.0))))
    return EditPlan(reply="Done." if ops else "", operations=ops)


__all__ = ["edit_scene", "PlannerError"]
