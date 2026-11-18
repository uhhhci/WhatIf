"""Turns a free-text "What if this place ..." prompt plus a scene summary into a ScenePlan.

The plan says WHAT to generate (one asset prompt per kind of object) and HOW to
distribute it over the detected surfaces. The actual positions are computed on
the headset, because only the headset knows the exact plane geometry.
"""

from __future__ import annotations

import json
import logging
import re

from .config import settings
from .schemas import (
    AssetItem,
    Behaviour,
    Effect,
    EffectType,
    Facing,
    Layout,
    Placement,
    PlanRequest,
    ScenePlan,
    Surface,
)

log = logging.getLogger("whatif.planner")

SYSTEM_PROMPT = """\
You are the scene planner of "What if?", an augmented-reality app on a Magic Leap 2 headset.
The user stands somewhere in the real world, looks around and asks "What if this place ...".
Your job is to decide which virtual objects and effects make that idea visible in THEIR
surroundings. A separate pipeline generates every object from your `asset_prompt`: first as a
2D cut-out (image generation + background removal), optionally as a 3D model afterwards.

How to plan:
- Use the camera image (if given) and the list of detected surfaces to understand the place:
  indoor or outdoor, what is there, how much room there is. Fit counts and sizes to it.
- Decide the `scope` first:
  - single: the user wants one thing or a few ("a giraffe", "a cat on the desk", "a lamp there"):
    exactly what was asked, nothing more (one item kind, count as asked).
  - environment: the whole place should change ("a jungle", "a medieval castle hall", "the sea
    floor", "a cozy reading corner"). Then make the room really look like it: 8-15 object kinds,
    planned in layers, so it feels full and lived-in, not like a few props:
    1. centre: one or two big pieces in the free middle of the room (a long table, a throne, a big
       tree) - role hero;
    2. along the walls: furniture and large plants standing against the walls (layout edge,
       facing back_to_wall) - hero or filler;
    3. on the walls: real 3D things hanging at different heights (torches, shields, mounted antlers,
       shelves, stone blocks, hanging plants) - filler;
    4. floor clutter: many small things spread over the free floor (stones, crates, ferns, books,
       bones) - filler with `density`;
    5. ceiling: things hanging down (chandeliers, lianas, lanterns) where a ceiling was detected;
    6. table tops: small things on detected tables or on planned tables (candles, goblets, books);
    7. coats (the `coats` list, not `items`): the real room itself gets covered, the way the place
       would overgrow or fill it. 1-2 entries with layout `coat`: small pieces (0.05-0.3 m tall) lying
       close together on the floor
       or on table and seat tops (surface floor or table) - moss clumps and fallen leaves in a
       jungle, sand mounds and shells on the sea floor, straw and rushes in a tavern, snow drifts,
       rubble in a ruin; with `density` (very_dense covers the surface completely: sand in a desert,
       snow, a grass field). footprint_m is one patch: 0.8-1.5 m for ground like sand, snow or grass,
       0.2-0.4 m for clumps like moss, leaves or shells. If tables or seats were
       detected, also 0-1 entry with layout `drape`, surface table: something hanging down over
       their edges, 0.3-0.8 m long (ivy or lianas, seaweed strands, a rough cloth, icicles). The
       asset_prompt describes ONE piece: "a single flat clump of green forest moss", "a single long
       strand of ivy hanging straight down". Coats are never painted surfaces.
    Fillers get `density` (sparse / normal / dense / very_dense) instead of an exact count: the
    headset computes how many fit. A jungle is very_dense, a castle hall normal, a tidy office sparse.
    The user stays in a free circle and keeps a free walking path; never block the whole floor with
    huge items.
- Each object kind costs one generation (a few seconds), instances are free: prefer many
  instances of a kind over many kinds.
- `role`: hero for prominent pieces, filler for the many small things (they get lighter 3D models).
- `behaviour`: living things and machines get a small state machine from building blocks, so the
  scene feels alive; still objects (furniture, stones, buildings) get none. Plants: one state with
  idle sway. Animals and people: breathe and look_at_user while standing, plus movement that fits
  them, e.g. a dog: state "idle" (breathe, look_at_user; next: user_looks -> "come"), "come" (move
  to_user, gait trot, speed 0.8; next: arrived -> "idle"); a shy cat flees when looked at; birds
  fly in circles; a car drives around (wander, gait roll, speed 1.2); fish swim. Animals should
  visibly do something within a few seconds: alternate standing and moving with timer events
  (e.g. "rest" 6 s -> "roam" (wander, slow) -> arrived -> "rest"), unless the user wants it still.
  People who are meant to stand in front of the user stay in place (breathe, look_at_user). Vary
  it with the personality the user asks for. Things that move are generated in side view facing right.
  `parts` adds motion to clearly separate parts (a dog: tail:wag, head:nod; a car: wheels:spin; a
  bird: wings:flap); leave it empty for compact objects.
  `web_search` names one specific real thing whose exact look matters (a landmark, a car model, a
  famous artwork, a historical figure such as Goethe) so real pictures can be used; web_kind person
  for people. Leave it empty for generic things ("a chair", "a dog").
- Sound: an environment gets an `ambience` (its background sound). Give `sound` only to a few
  characteristic objects (a ticking clock, a fountain, a crackling fireplace, a purring cat) -
  never to plants, stones, furniture or most fillers: a room full of sounds is noise.
- `asset_prompt` is always in English, whatever the language of the user's prompt (the image
  model only understands English). It is at most 25 words (the image model ignores everything after ~60 words, and
  the pipeline adds its own framing). It describes ONE isolated object, fully visible, as a photo or illustration
  (photo for realistic ideas, illustration only when the idea is playful). Never describe a
  background, ground, scene or several objects: ONE single specimen, even if the user wants many
  (`count` says how many copies are placed): "a single clownfish", "a single climbing hold" - never
  "a school of fish" or "climbing holds". But describe the object AS IT EXISTS IN THE
  SCENARIO - its origin, era, state and style - because the image model only sees this text and
  otherwise draws the everyday version (a plant alone becomes a potted houseplant, a bench a
  modern garden bench): "wild banana plant from a tropical rainforest, growing from the ground",
  "rough medieval oak bench, worn by centuries", "dented metal sci-fi cargo crate". Good: "a young linden tree with a full green
  crown, realistic photo". Bad: "trees along the street". Unusual features (colours, clothes,
  materials) go FIRST - the image model weights the first words most: "a purple cow wearing
  sunglasses, full body, realistic photo". Animals and people: always "full body".
- Give each object kind a realistic `footprint_m` (its largest horizontal size); objects are kept
  at least that far apart, so big things (sofas, dinosaurs, trees) need honest values.
- Heights are real-world sizes in metres. Respect the space: no 10 m trees in a 3 m room.
- Relate objects to the place and to each other with `target_id` (detected surfaces are S1, S2,
  ...; the same numbers are drawn into the camera image where those surfaces are):
  - chairs around a detected table: surface floor, layout around, target_id the table, facing
    toward_target, count fitting the table's size (about one chair per 0.6 m of its edge);
  - a lamp or plant beside a sofa or seat: layout beside, target_id the seat;
  - vases on one particular table: surface table, target_id that table;
  - a new group, e.g. a dining table with chairs in an empty space: plan the table as its own item
    and use the table item's id as target_id for the chairs (list the table first).
  - a real thing that is VISIBLE IN THE CAMERA IMAGE but isn't a detected surface (a keyboard, a
    person's head, a bin, a shelf): target_id "IMG x,y", the point where it is in the image (x, y
    from 0 to 1, measured from the top left; the tick marks along the image edges help). The
    headset finds that thing in 3D and its top by itself. Always point AT THE THING ITSELF, in the
    middle of its visible part (a table's front edge or top, the middle of a head, the middle of a
    keyboard) - never at the empty space above it, which is the wall behind it in 3D. Layout single
    = ON TOP of it (a mouse on the keyboard, a crown on a person's head), beside = on the floor next
    to it (a second bin next to the bin - point at the bin's lower half), around = in a ring around it,
    under = on the floor beneath it (a cat under the table, a box under the desk: the headset sizes
    it to fit below; for a detected table use its S-number).
  - "there" / "over there" / "da" / "dort" / "hier drüben": the point the user looked at while
    speaking (given as "looked at" when known): target_id "GAZE", layout single.
  - A thing the user NAMES comes first ("on the table", "auf dem Schrank"): its S-number if it is a
    detected surface, else its point in the camera image (IMG x,y). GAZE only when the user doesn't
    name the place ("there", "here", "da", "dort").
  Use layout around/beside/under only with a target_id ("under the table" = layout under, target
  the table; never on top of it).
- Only use surfaces listed under "You can place objects on". The camera image may show walls,
  tables or a ceiling the headset has not detected yet; objects planned for those cannot be placed.
  Prefer objects that work on the available surfaces (e.g. floor plants instead of hanging plants).
- When walls are available, an environment also uses them: climbing vines and moss in a jungle,
  torches, banners and shields in a castle hall, shelves, pictures or lamps in a room.
- `note_for_user` and `interpretation` are for the user: never mention technical details
  (planes, S-numbers, surfaces, detection, counts). A note only when the user must do something.
- Big things may be taller than the room (a tree reaches through the ceiling). Prefer trees,
  palms and plants whose leaves start above head height over wide crowns at eye level. Flying
  animals fly below the ceiling.
- Start with what makes the place recognisable at a glance: name for yourself the 2-4 elements
  that define it and plan them first, prominently and in realistic numbers, on the surfaces where
  they belong (a bouldering gym: many climbing holds all over the walls, thick crash mats along
  them; a library: tall shelves full of books along the walls; an aquarium: fish and water
  plants). Secondary details come after them.
- Keep the middle of the room free for the user: big things (trees, pillars, wardrobes, large
  furniture) stand in the outer area or along the walls (layout edge for furniture); only small
  things go in the middle.
- Pick the surface the object physically stands on or hangs from. Climbing plants or posters
  go on walls, lamps can hang from the ceiling, flower pots fit on tables.
- `surface: hand` = on the user's own hand ("a bird on my hand", "a butterfly in my palm"): one
  small thing (count 1, layout single, at most 0.25 m); it stays on the hand and moves with it.
- Choose `facing` so objects stand the way they would in reality: `random` for things without a
  front (plants, trees, rocks, round tables), `toward_user` for things that face people (animals,
  people, statues, signs, displays), `back_to_wall` for furniture that stands against a wall
  (sofas, shelves, cabinets, benches by a wall); give those `surface: floor` and `layout: edge`
  so they end up near walls.
- {objects_rule}{thin_rule}
- Changes of the room's surfaces or a layer filling the room are effects, not objects. There
  are no fixed effects: build each look from blocks (colours, pattern, motion, glow, reflect,
  edge, particles):
  - `level`: a horizontal layer at water_height_m above the floor: a flood (blue-green, ~0.6
    opacity, pattern waves, motion ripple, reflect 0.6, edge foam), fog on the floor (white, 0.25,
    noise, flow, slow), a sea of lava;
  - `surface`: a look over detected surfaces (surfaces: floor / wall / ceiling / S-numbers): a lava
    floor (crust #1a0d08 + cracks #ff6a00, pattern cells, flow slowly, glow 0.8, edge glow,
    particles rise orange), an ice floor (pale blue, cells, reflect 0.7, edge frost), a starry
    ceiling (dark blue 0.9, speckle, glow 0.9), a snowy floor (white, noise, particles fall);
  - these are examples: combine the blocks freely for whatever the user imagines.
  A `surface` effect only when the wish is about the room's surfaces themselves (the floor is
  lava, a frozen floor, snow on the ground, painted walls, a starry ceiling). A theme (a jungle, a
  castle, a spaceship) is made of objects - never a coloured floor or walls for it. Exception: a place
  whose GROUND defines it (a desert or beach: sand; a snowfield; the sea floor; the moon) also gets a
  `surface` effect on the floor with that ground (sand: warm beige #d8b98a with #c49a62 and #ead3a8,
  pattern noise or waves, ~0.7 opacity), plus objects as usual.
  Particles only where something really emits them (embers over lava, falling snow) - never as
  decoration. Objects that emit (a fire pit, torches, a fountain) use the item's `emit` instead.
  `tint` darkens and colours the whole view: only when the user asks for light or mood (night,
  a storm, a sunset, under water), never to colour walls or a theme.
- Changing the colour of the real room (painted walls, a red floor, a dark ceiling) is a
  `surface` effect (plain colour: pattern none), not an object: tint_hex the colour, surfaces the S-numbers to paint or a
  kind ("wall" = all detected walls), strength the opacity (0.8 for normal paint, 0.95 to cover a
  dark surface, 0.5 for a glaze). Only detected surfaces can be painted; if the wanted kind was
  not detected, say in note_for_user that the user should look at it first. Painting needs no
  items; add objects only if the idea asks for more than colour. Use a plain-colour surface ONLY when the
  user asks for a colour or paint ("blue walls", "paint the floor red"). Never use it to suggest a
  material or a theme (stone, brick, wood, medieval, jungle): paint is a flat coloured layer and
  looks like a grey plate, not like stone. Themes are made of 3D objects alone.
- Removing real objects (e.g. "fewer cars") is not possible yet. Visualise the positive side
  instead (bike racks, benches, trees where cars were) and say so in `note_for_user`.
- Keep `interpretation` and `note_for_user` to one short sentence each; they are shown in the
  headset. Write them in the language of the user's prompt (English prompt -> English text,
  German prompt -> German text).
- `request`: true for a "What if ..." prompt. A prompt marked as spoken WITHOUT "What if" may be
  talk to other people in the room: set `request` true if it wishes for a change of the
  surroundings - a whole scene ("a jungle here would be cool", "show me a medieval hall", "Mach
  hier einen Strand draus") or single things, also as a direct instruction to put, hang or place
  something ("hang a portrait of Goethe on the wall", "put a plant there", "I'd love some plants
  here", "stell einen Tisch in die Ecke", "häng ein Bild an die Wand"). An instruction is a
  request even if the surface it needs is missing (plan it anyway; the note explains). Comments,
  questions to people, chatter about anything else ("wow, nice", "where is my phone?", "the
  meeting is at three", "can you pass me the water?") are not requests: `request` false, no
  items, no effects, empty texts. When unsure whether it is meant for the scene, false.
"""


ONLY_3D_RULE = (
    "Every object becomes a real 3D model, so plan only solid, free-standing or hanging things with a\n"
    "  real volume (trees, plants, animals, people, furniture, lamps, vehicles, statues, a clock or\n"
    "  a shelf on a wall). Never plan flat things or coverings: no posters, paintings, signs, wallpaper,\n"
    "  ivy or moss on a wall, stone or brick wall panels, wall or floor textures, rugs, carpets or water\n"
    "  surfaces - they would show up as a flat plate in the room. Colours of real walls / floors are\n"
    "  a plain-colour surface effect; a \"stone wall\" idea becomes e.g. stone pillars or a low dry-stone wall segment\n"
    "  standing on the floor, or is left out. Set `want_3d` to true for every item."
)
MIXED_RULE = (
    "Use `want_3d` for objects users will walk around (trees, shrubs, animals, furniture, carts,\n"
    "  statues). Keep flat things (posters, ivy, signs, paintings) 2D."
)


def system_prompt() -> str:
    # TripoSR reconstructs thin parts badly; SPAR3D and external tools handle them.
    if settings.mesh_provider != "triposr":
        thin = ""
    elif settings.only_3d:
        thin = ("\n  Avoid thin or wiry things (bicycles, fences, railings, lamp posts, bare branches):"
                " the current 3D reconstruction cannot handle thin parts.")
    else:
        thin = ("\n  Also keep thin or wiry things (bicycles, fences, railings, lamp posts, bare branches) 2D:"
                " the current 3D reconstruction cannot handle thin parts.")
    rule = ONLY_3D_RULE if settings.only_3d else MIXED_RULE
    return SYSTEM_PROMPT.replace("{objects_rule}", rule).replace("{thin_rule}", thin)


def _scene_text(req: PlanRequest) -> str:
    scene = req.scene
    lines = [f'User prompt: "{req.prompt}"', ""]
    if req.english_only:
        lines += ["Write `interpretation` and `note_for_user` in English, whatever language the prompt is in.", ""]
    if req.looked_at:
        lines += [f"While speaking, the user looked at: {req.looked_at} (target_id GAZE).", ""]
    if req.natural:
        lines += ["Spoken WITHOUT \"What if\" while nothing is shown: decide whether it is a request "
                  "(see `request`).", ""]
    if scene.head_height_m is not None:
        lines.append(f"Estimated head height above floor: {scene.head_height_m:.2f} m")
    if scene.mesh_extent_m is not None:
        lines.append(f"Reconstructed surroundings reach about {scene.mesh_extent_m:.1f} m around the user")
    if scene.planes:
        lines.append("Detected surfaces (S-numbers are also drawn into the camera image where visible):")
        for short, p in zip(short_ids(req), scene.planes[:40]):
            where = f"{p.distance_m:.1f} m away"
            if p.bearing_deg is not None:
                b = p.bearing_deg
                side = "straight ahead" if abs(b) < 10 else (f"{abs(b):.0f} deg to the {'right' if b > 0 else 'left'}"
                                                             if abs(b) < 135 else "behind you")
                where += f", {side}"
            height = (f"{p.height_above_floor_m:.2f} m above the floor" if p.height_above_floor_m is not None
                      else f"{p.height_rel_head_m:+.1f} m relative to head")
            seen = ", visible in the image" if p.view_x is not None else ""
            lines.append(f"- {short}: {p.label}, {p.width_m:.1f} x {p.height_m:.1f} m, {where}, {height}{seen}")
    else:
        lines.append(
            "No surfaces detected (e.g. outdoors in sunlight). Objects will be placed on an "
            "assumed ground plane around the user."
        )
    lines.append("You can place objects on: " + ", ".join(sorted(available_surfaces(req))) + ".")
    if req.image_jpeg_b64 is None:
        lines.append("No camera image available.")
    return "\n".join(lines)


def _font(size: int):
    from PIL import ImageFont

    for name in ("arial.ttf", "DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    try:
        return ImageFont.load_default(size=size)
    except TypeError:  # Pillow < 10.1
        return ImageFont.load_default()


def mark_surfaces(req: PlanRequest) -> str:
    """Draw the surfaces' S-numbers into the camera image where they are ("set-of-marks"), so the
    planner can tell which listed surface is the table it sees, and tick marks with 0.1 steps
    along the edges, so it can name image points ("IMG 0.62,0.71") for real things it sees.
    Returns base64 JPEG."""
    return mark_image(req.image_jpeg_b64, list(zip(short_ids(req), req.scene.planes)))


def mark_image(image_b64: str, planes: list) -> str:
    import base64
    import io

    from PIL import Image, ImageDraw

    img = Image.open(io.BytesIO(base64.b64decode(image_b64))).convert("RGB")
    draw = ImageDraw.Draw(img)
    w, h = img.size
    # coordinate ticks: short lines into the image, labelled 0.1 .. 0.9 (thin, so the view stays clear)
    tick = max(6, w // 70)
    small = _font(max(9, w // 55))
    for i in range(1, 10):
        x, y = w * i / 10, h * i / 10
        for line in (((x, 0), (x, tick)), ((x, h - tick), (x, h)), ((0, y), (tick, y)), ((w - tick, y), (w, y))):
            draw.line(line, fill=(0, 0, 0), width=3)
            draw.line(line, fill=(255, 255, 255), width=1)
        label = f".{i}"
        for pos, anchor in (((x, tick + 1), "mt"), ((tick + 2, y), "lm")):
            draw.text(pos, label, fill=(255, 255, 255), font=small, anchor=anchor, stroke_width=2, stroke_fill=(0, 0, 0))
    r = max(10, w // 40)
    font = _font(int(r * 1.2))
    for sid, p in planes:
        if p.view_x is None or p.view_y is None:
            continue
        x, y = p.view_x * w, p.view_y * h
        draw.ellipse((x - r * 1.6, y - r, x + r * 1.6, y + r), fill=(255, 220, 0), outline=(0, 0, 0), width=2)
        draw.text((x, y), sid, fill=(0, 0, 0), font=font, anchor="mm")
    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=85)
    return base64.b64encode(buf.getvalue()).decode()


_IMG_POINT = re.compile(r"^\s*IMG\s*[:(]?\s*([0-9]*\.?[0-9]+)\s*[,;\s]\s*([0-9]*\.?[0-9]+)\s*\)?\s*$", re.IGNORECASE)


def parse_image_point(target: str | None) -> str | None:
    """'IMG 0.62,0.71' (a real thing seen in the camera image) -> canonical 'img:0.620,0.710',
    which the headset turns into a 3D point; None if it isn't one."""
    m = _IMG_POINT.match(target or "")
    if not m:
        return None
    x, y = (min(1.0, max(0.0, float(v))) for v in m.groups())
    return f"img:{x:.3f},{y:.3f}"


def short_ids(req: PlanRequest) -> list[str]:
    """S1, S2, ... for the detected planes (the headset's ids are long and meaningless to the planner)."""
    return [f"S{i + 1}" for i in range(min(len(req.scene.planes), 40))]


def _resolve_targets(plan: ScenePlan, req: PlanRequest) -> ScenePlan:
    """Map S-numbers back to the headset's plane ids, check item targets, and order items so that
    targets are placed before the objects that refer to them."""
    to_plane = dict(zip(short_ids(req), (p.id for p in req.scene.planes)))
    label_of = {p.id: p.label for p in req.scene.planes}
    # which plane labels an item asking for a surface type may target ("on S3")
    fits = {Surface.floor: {"floor", "table", "seat", "unknown"}, Surface.table: {"table", "seat", "unknown"},
            Surface.wall: {"wall", "unknown"}, Surface.ceiling: {"ceiling", "unknown"}}
    item_ids = {i.id for i in plan.items}
    tables = [p for p in req.scene.planes if p.label in ("table", "seat")]
    for item in plan.items:
        p = item.placement
        t = (p.target_id or "").strip()
        plane = to_plane.get(t.upper())
        if plane and p.layout not in (Layout.around, Layout.beside, Layout.under) and \
                label_of.get(plane, "unknown") not in fits.get(p.surface, {label_of.get(plane, "unknown")}):
            # seen: a giraffe on the floor with the ceiling as target - hung below the floor
            log.info("dropping target %s (%s) of %s: doesn't fit surface %s",
                     t, label_of.get(plane), item.id, p.surface.value)
            t, plane = "", None
        if t.upper() in ("GAZE", "THERE") and (named := named_surface(req.prompt, req)):
            # "on the table" without "there": the named table, not where the gaze happened to be
            log.info("%s: the named surface instead of the gaze point", item.id)
            plane, t = named, ""
        elif not t and p.surface == Surface.table and (named := named_surface(req.prompt, req)):
            plane = named  # "on the table" without a target: that table, not any table or seat
        point = parse_image_point(t) if req.image_jpeg_b64 else None
        if t.upper() in ("GAZE", "THERE") and req.looked_at:
            p.target_id = "gaze"  # the point the user looked at; the headset knows where it is
        elif point:
            p.target_id = point  # a real thing in the camera image; the headset finds it in 3D
        elif plane:
            p.target_id = plane
        elif t and t in item_ids and t != item.id:
            p.target_id = t  # another item of this plan
        else:
            p.target_id = None
        if p.layout in (Layout.around, Layout.beside, Layout.under) and label_of.get(p.target_id) in ("floor", "wall", "ceiling"):
            # "next to the floor" means nothing (seen: a giraffe "beside" the floor, meant next to the
            # user): a normal placement in front of the user instead
            log.info("%s: %s %s makes no sense, placing it normally", item.id, p.layout.value, label_of[p.target_id])
            p.target_id = None
            p.layout = Layout.single if p.count == 1 else Layout.scatter
            if p.facing == Facing.toward_target:
                p.facing = Facing.toward_user
        needs_target = p.layout in (Layout.around, Layout.beside, Layout.under) or p.facing == Facing.toward_target
        if needs_target and p.target_id is None:
            if tables:  # most likely meant: the biggest table
                p.target_id = max(tables, key=lambda x: x.width_m * x.height_m).id
            else:
                if p.layout in (Layout.around, Layout.beside, Layout.under):
                    p.layout = Layout.scatter
                if p.facing == Facing.toward_target:
                    p.facing = Facing.toward_user
        if p.layout in (Layout.around, Layout.beside, Layout.under):
            p.surface = Surface.floor  # things around / beside something stand on the floor

    # targets first (stable otherwise)
    order, placed = [], set()
    pending = list(plan.items)
    while pending:
        progressed = False
        for item in list(pending):
            t = item.placement.target_id
            if t not in item_ids or t in placed:
                order.append(item); placed.add(item.id); pending.remove(item); progressed = True
        if not progressed:  # cycle: break it
            item = pending.pop(0); item.placement.target_id = None; order.append(item); placed.add(item.id)
    plan.items = order
    return plan


_THERE = re.compile(r"\b(there|here|over there|this spot|that spot|da|dort|hier|dahin|dorthin|hierhin|"
                    r"drüben|drueben)\b", re.IGNORECASE)
_EXISTENTIAL = re.compile(r"\bthere(?:'s|\s+(?:is|are|was|were|would|will|could|might|should|be)\b)", re.IGNORECASE)
_TABLE_WORDS = re.compile(r"\b(tables?|desks?|counters?|sideboards?|dressers?|shelf|shelves|benches|bench|"
                          r"nightstands?|(\w*tisch(es|e)?)|theke|kommode|regal|anrichte|bank|ablage|fensterbank|"
                          r"windowsill)\b", re.IGNORECASE)


def named_surface(utterance: str, req) -> str | None:
    """The headset's id of the detected table-like surface the user names ("on the table", "auf den
    Tisch") when they don't say "there": a named thing beats the point the gaze happened to hit (seen:
    a coffee cup asked for on the table landed on the floor beside it). The nearest one if several."""
    # "there was a cup" is not "over there" (seen: "what if there was a coffee cup on the table" ignored the table)
    if not utterance or _THERE.search(_EXISTENTIAL.sub(" ", utterance)) or not (_TABLE_WORDS.search(utterance) or _SEAT_WORDS.search(utterance)):
        return None
    # a seat only when one is named: chairs pushed under a table are often nearer than its top (seen:
    # a coffee cup and a cola bottle "on the table" landed on a chair seat under it)
    seat_named = bool(_SEAT_WORDS.search(utterance))
    tables = [p for p in req.scene.planes if p.label == ("seat" if seat_named else "table")]
    if not tables:
        tables = [p for p in req.scene.planes if p.label in ("table", "seat")]
    return min(tables, key=lambda p: p.distance_m).id if tables else None


_SEAT_WORDS = re.compile(r"\b(chairs?|seats?|sofas?|couch(es)?|bench(es)?|stools?|st(ü|ue)hle?|stuhl|sessel|"
                         r"bank|hocker|sitz)\b", re.IGNORECASE)


def available_surfaces(req: PlanRequest) -> set[str]:
    """Surface types the headset can place on right now. The floor always works (an assumed ground
    plane stands in when none was detected); "horizontal" and "any" fall back to it."""
    labels = {p.label for p in req.scene.planes}
    available = {Surface.floor.value, Surface.horizontal.value, Surface.any.value, Surface.hand.value}
    if labels & {"table", "seat"}:
        available.add(Surface.table.value)
    if "wall" in labels:
        available.add(Surface.wall.value)
    if "ceiling" in labels:
        available.add(Surface.ceiling.value)
    return available


_NEEDS = {"wall": "a wall", "ceiling": "a ceiling"}


def _enforce_surfaces(plan: ScenePlan, req: PlanRequest) -> ScenePlan:
    """Keep the plan to surfaces that exist: table objects move to the floor, wall and ceiling
    objects (no sensible fallback) are dropped with a hint to look around."""
    available = available_surfaces(req)
    item_ids = {i.id for i in plan.items}
    kept, missing = [], []
    for item in plan.items:
        surface = item.placement.surface.value
        on_thing = (item.placement.target_id in item_ids or item.placement.target_id == "gaze"
                    or parse_image_point(item.placement.target_id))
        if surface in available or on_thing:  # on another planned object or a real thing in the image
            kept.append(item)
        elif surface == Surface.table.value and item.placement.layout == Layout.drape:
            kept.append(item)  # only real table / seat edges: the headset tries again when it finds one
        elif surface == Surface.table.value:
            item.placement.surface = Surface.horizontal
            kept.append(item)
        else:
            # kept: the headset estimates walls from its room mesh and places such items as soon as
            # it knows a fitting surface (seen: climbing holds dropped because no wall plane was found)
            kept.append(item)
            missing.append(f"{item.label} needs {_NEEDS.get(surface, 'a ' + surface)}")
    plan.items = kept
    if missing:
        hint = "; ".join(missing) + " - look at it for a moment, then it appears there."
        plan.note_for_user = (plan.note_for_user + " " if plan.note_for_user else "") + hint[0].upper() + hint[1:]
    return plan


# Flat things the planner should not plan with "3D only" (it sometimes still does - seen: twelve
# posters). Wall art becomes a framed relief with depth; coverings (rugs, wallpaper) are dropped.
_FLAT_ART = re.compile(r"\b(posters?|paintings?|pictures?|artworks?|canvas(es)?|murals?|tapestr(y|ies)|banners?|"
                       r"signs?|signboards?|calendars?|plakate?|gemälde|bild(er)?|wandbild(er)?|schild(er)?)\b",
                       re.IGNORECASE)
_FLAT_COVER = re.compile(r"\b(rugs?|carpets?|mats?|wallpapers?|tiles?|textures?|floor covering|teppich(e)?|tapete(n)?|"
                         r"fliesen?|bodenbelag)\b", re.IGNORECASE)


_GROUND_PIECE = re.compile(r"\b(sand ?dunes?|dunes?|sand (piles?|mounds?|heaps?|hills?|banks?)|terrain|ground|"
                           r"snow ?(drifts?|banks?|piles?)|d(ü|ue)nen?|sandhaufen|sandh(ü|ue)gel|schneewehen?)\b",
                           re.IGNORECASE)


def clean_behaviour(b: Behaviour | None) -> Behaviour | None:
    """At most 4 states, transitions only to existing states, sane speeds."""
    if b is None or not b.states:
        return None
    b.states = b.states[:4]
    names = {st.name for st in b.states}
    for st in b.states:
        st.speed = max(0.05, min(4.0, st.speed))
        # without a walking animation, legs that don't move look wrong: only things that fly, swim or
        # roll (vehicles) move around; animals that walk stay in place (breathe, look at the user)
        if st.gait not in ("fly", "swim", "roll"):
            st.move = "none"
        st.next = [t for t in st.next if t.goto in names][:4]
        for t in st.next:
            t.value = max(0.0, min(120.0, t.value))
    return b


def _moves(b: Behaviour | None) -> bool:
    return b is not None and any(st.move != "none" for st in b.states)


_CREATURE = re.compile(
    r"\b(animals?|birds?|parrots?|owls?|eagles?|falcons?|hawks?|pigeons?|doves?|ducks?|geese|goose|swans?|"
    r"flamingos?|penguins?|chickens?|hens?|roosters?|dogs?|pupp(y|ies)|cats?|kittens?|horses?|pon(y|ies)|"
    r"cows?|pigs?|sheep|goats?|rabbits?|bunn(y|ies)|mice|mouse|rats?|squirrels?|foxes|fox|wol(f|ves)|bears?|"
    r"lions?|tigers?|leopards?|cheetahs?|elephants?|giraffes?|zebras?|monkeys?|apes?|gorillas?|deer|moose|"
    r"camels?|kangaroos?|koalas?|pandas?|dinosaurs?|dragons?|unicorns?|fish(es)?|sharks?|whales?|dolphins?|"
    r"turtles?|tortoises?|frogs?|lizards?|snakes?|crocodiles?|alligators?|butterfl(y|ies)|bees?|dragonfl(y|ies)|"
    r"robots?|cars?|taxis?|bus(es)?|trucks?|vans?|bikes?|bicycles?|motorcycles?|scooters?|trains?|trams?|boats?|"
    r"ships?|planes?|airplanes?|helicopters?|drones?|tractors?|"
    r"hunde?|katzen?|vögel|vogel|pferde?|fische?|autos?|schiffe?)\b", re.IGNORECASE)
_STILL_THING = re.compile(r"\b(statues?|figurines?|sculptures?|toys?|plush|painting|picture|poster|houses?|"
                          r"cages?|feeders?|nests?|skeletons?|model kit)\b", re.IGNORECASE)


def could_move(item: AssetItem) -> bool:
    """An animal or vehicle (not a statue or toy of one): it may be told to move later."""
    text = f"{item.label} {item.asset_prompt}"
    return bool(_CREATURE.search(text)) and not _STILL_THING.search(text)


def _solid(item: AssetItem) -> AssetItem | None:
    text = f"{item.label} {item.asset_prompt}"
    if item.placement.layout not in (Layout.coat, Layout.drape) and _GROUND_PIECE.search(item.label):
        # seen: a "sand dune" turned into a tall slab with ripples; the ground is a coat, not an object
        log.info("dropping ground piece %s (%r): the ground is a coat", item.id, item.label)
        return None
    if _FLAT_COVER.search(text):
        log.info("dropping flat item %s (%r): a covering, not a 3D object", item.id, item.label)
        return None
    if _FLAT_ART.search(text) and "shadow box" not in item.asset_prompt.lower():
        subject = item.label.strip() or "artwork"
        item.asset_prompt = (f"a deep wooden shadow box frame with a colourful 3D relief of {subject}, "
                             "seen from the front, realistic photo")[:300]
        item.placement.footprint_m = max(item.placement.footprint_m, 0.15)
        log.info("turning flat item %s (%r) into a framed relief", item.id, item.label)
    return item


def normalise_effect(e: Effect) -> Effect:
    """Older effect names become the general ones; the blocks are clamped and colours cleaned."""
    if e.type == EffectType.water_level:
        e.type = EffectType.level
    elif e.type == EffectType.surface_paint:
        e.type = EffectType.surface
    if e.type == EffectType.level and e.water_height_m is None:
        e.water_height_m = 0.5
    e.colors = [c for c in (clean_hex(c) for c in e.colors[:3]) if c]
    e.glow_hex, e.edge_hex, e.particle_hex = (clean_hex(e.glow_hex) if e.glow_hex else None,
                                              clean_hex(e.edge_hex) if e.edge_hex else None,
                                              clean_hex(e.particle_hex) if e.particle_hex else None)
    e.pattern_scale_m = max(0.02, min(5.0, e.pattern_scale_m))
    e.speed = max(0.0, min(2.0, e.speed))
    e.glow, e.reflect = max(0.0, min(1.0, e.glow)), max(0.0, min(1.0, e.reflect))
    e.particle_amount = max(0.0, min(1.0, e.particle_amount))
    return e


_TECH = re.compile(r"\bS\d+\b|\bplanes?\b|\bebenen?\b|\bsurfaces?\b|\bfläche|\bdetect|\berkannt|\bmesh",
                   re.IGNORECASE)
_LOOK = re.compile(r"\blook\b|\bschau", re.IGNORECASE)


# groups in an image description become a cluster in one cut-out (seen: a school of fish, a bunch of
# climbing holds as one object); the copies are placed by `count` instead
_GROUP = re.compile(r"^\s*(?:(?:a|an|some)\s+)?(?:group|school|flock|herd|swarm|cluster|set|collection|bunch|"
                    r"pack|colony|several|many|multiple|numerous|lots|\d+|two|three|four|five|six|seven|eight|"
                    r"nine|ten|eleven|twelve|fifteen|twenty|thirty|fifty|hundreds?|dozens?)\s+(?:of\s+)?", re.IGNORECASE)
_SINGLE = re.compile(r"^\s*(a|an|one)\s+(single\s+)?", re.IGNORECASE)


_HANGS = re.compile(r"hang|hanging|chandelier|lamp|lantern|light|pendant|banner|flag|garland|vine|liana|ivy|moss|"
                    r"bat|spider|cobweb|web|mobile|bird|parrot|butterfl|cloud|balloon|star|planet|moon|sign|"
                    r"wind chime|chime|bell|curtain|net|rope|chain|cage|nest|icicle|stalactite|disco|fan",
                    re.IGNORECASE)


def _single(prompt: str) -> str:
    """'a school of clownfish ...' -> 'a single clownfish ...'; 'colorful climbing holds' -> 'a single
    colorful climbing holds' (the image model reads 'single'; a stack or a pile stays one object)."""
    p = _GROUP.sub("", prompt or "", count=1)
    if not _SINGLE.match(p):
        p = "a single " + p
    elif not re.match(r"^\s*(a|an|one)\s+single\b", p, re.IGNORECASE):
        p = _SINGLE.sub("a single ", p, count=1)
    # the main noun singular too: "a single colorful climbing holds" still showed a cluster
    head, sep, rest = p.partition(",")
    words = head.split()
    # the noun: the last word before a preposition or participle ("red berries on a branch")
    end = len(words)
    for i in range(3, len(words)):
        if words[i].lower() in _AFTER_NOUN:
            end = i
            break
    else:  # "wooden boxes stacked": a participle at the very end follows the noun
        if len(words) > 3 and words[-1].lower().endswith(("ed", "ing")) and words[-2].lower().endswith("s"):
            end = len(words) - 1
    k = end - 1
    if k >= 2 and words[k].lower() not in _NOT_PLURAL and not words[k].lower().endswith(("ous", "less")):
        words[k] = _singular(words[k])
    return (" ".join(words) + sep + rest)[:300]


_AFTER_NOUN = {"on", "in", "with", "of", "from", "at", "for", "by", "under", "over", "near", "inside", "made",
               "and", "or", "that", "which", "standing", "hanging", "sitting", "lying", "growing", "floating"}


# words ending in s that are not plurals (or plurals without a simple singular)
_NOT_PLURAL = {"glass", "grass", "moss", "cactus", "octopus", "bus", "dress", "chess", "iris", "lotus",
               "walrus", "platypus", "asparagus", "hibiscus", "fungus", "compass", "fortress", "harness",
               "mattress", "bonus", "canvas", "atlas", "pegasus", "series", "species", "jeans", "pants",
               "scissors", "glasses", "news", "lens", "chassis", "abyss", "princess", "goddess", "class",
               "boss", "cross", "press", "mass", "brass", "lass", "hippopotamus", "rhinoceros", "tennis",
               "physics", "mathematics", "gas", "his", "this", "is", "as", "was", "has", "its", "us"}


def _singular(word: str) -> str:
    """holds -> hold, berries -> berry, leaves -> leaf, boxes -> box (only clear English plurals)."""
    w = word.lower()
    if len(w) < 4 or not w.endswith("s") or w.endswith("ss") or w.endswith("us") or w.endswith("is"):
        return word
    if w.endswith("ies"):
        return word[:-3] + "y"
    if w.endswith("ves") and len(w) > 5:
        return word[:-3] + "f"
    if w.endswith(("ches", "shes", "xes", "zes", "sses")):
        return word[:-2]
    return word[:-1]


def _coat(item: AssetItem) -> None:
    """Pieces of a coat (moss tufts, ivy strands): many small still things on the room's surfaces.
    Coats lie on the floor or on table / seat tops; drapes hang from table / seat edges."""
    p = item.placement
    item.role = "filler"
    item.behaviour, item.sound, item.parts, item.web_search, item.emit = None, "", [], "", "none"
    p.facing = Facing.random
    # Cut-outs, no 3D model: a coat piece lies flat on the surface (a top-down picture), a drape hangs
    # flat against the edge. As 3D models they were upright green chips (seen on the headset).
    item.want_3d = False
    if p.layout == Layout.coat:
        if p.surface not in (Surface.floor, Surface.table, Surface.horizontal):
            p.surface = Surface.floor
        p.height_m = max(0.03, min(0.35, p.height_m))
        # the size of one lying patch: ground (sand, snow, grass) up to 1.5 m, clumps (moss, leaves) smaller
        p.footprint_m = max(0.1, min(1.5, p.footprint_m))
        p.density = p.density or "dense"
        if not re.search(r"from directly above|top-down", item.asset_prompt, re.IGNORECASE):
            item.asset_prompt = (item.asset_prompt.rstrip(". ") + ", flat, seen from directly above, top-down view")[:300]
    else:
        p.surface = Surface.table
        if p.target_id and not p.target_id.startswith("S"):
            p.target_id = None  # only detected tables and seats have edges to hang from
        p.height_m = max(0.15, min(1.0, p.height_m))
        p.footprint_m = max(0.05, min(0.4, p.footprint_m))
        p.density = p.density or "normal"
        if not re.search(r"hang|drap", item.asset_prompt, re.IGNORECASE):
            item.asset_prompt = (item.asset_prompt.rstrip(". ") + ", hanging straight down")[:300]
        if "front view" not in item.asset_prompt.lower():
            item.asset_prompt = (item.asset_prompt.rstrip(". ") + ", front view")[:300]


def _user_text(text: str) -> str:
    """Drops sentences about technical details; keeps hints the user can act on ("look at the wall")."""
    parts = re.split(r"(?<=[.!?])\s+", (text or "").strip())
    return " ".join(p for p in parts if p and (not _TECH.search(p) or _LOOK.search(p)))


def _sanitize(plan: ScenePlan) -> ScenePlan:
    """Clamp values the schema cannot constrain, and make ids unique."""
    seen: set[str] = set()
    for item in plan.items:
        base = re.sub(r"[^a-z0-9_]+", "_", item.id.lower()).strip("_") or "item"
        uid, n = base, 2
        while uid in seen:
            uid, n = f"{base}_{n}", n + 1
        seen.add(uid)
        item.id = uid
        if settings.only_3d and settings.mesh_provider != "none":
            item.want_3d = True  # the cut-out only shows while the model is being made
        p = item.placement
        p.count = max(1, min(60, p.count))
        p.height_m = max(0.05, min(30.0, p.height_m))
        p.footprint_m = max(0.05, min(40.0, p.footprint_m))
        p.height_jitter = max(0.0, min(0.5, p.height_jitter))
        p.min_spacing_m = max(0.0, min(20.0, p.min_spacing_m))
        p.max_distance_m = max(1.0, min(50.0, p.max_distance_m))
    # coats come in their own list (so the local model thinks of them); they join the items here,
    # keeping their places when a long plan is cut
    coats = plan.coats[:3] if plan.scope == "environment" else []
    plan.coats = []
    for c in coats:
        c.id = re.sub(r"[^a-z0-9_]+", "_", c.id.lower()).strip("_") or "coat"
        if settings.only_3d and settings.mesh_provider != "none":
            c.want_3d = True
        if c.placement.layout not in (Layout.coat, Layout.drape):
            c.placement.layout = Layout.drape if c.placement.surface == Surface.table and \
                re.search(r"hang|drap", c.asset_prompt, re.IGNORECASE) else Layout.coat
    limit = 16 if plan.scope == "environment" else 6
    # an environment may have many kinds (layers), a single request only a few; big pieces first, so
    # the room builds up from its main pieces (generation runs in this order)
    plan.items = plan.items[: limit - len(coats)] + coats
    seen.clear()
    for item in plan.items:  # ids unique across items and coats
        base, uid, n = item.id, item.id, 2
        while uid in seen:
            uid, n = f"{base}_{n}", n + 1
        seen.add(uid)
        item.id = uid
    for item in coats:
        p = item.placement
        p.count = max(1, min(60, p.count))
        p.height_m = max(0.05, min(30.0, p.height_m))
        p.footprint_m = max(0.05, min(40.0, p.footprint_m))
        p.height_jitter = max(0.0, min(0.5, p.height_jitter))
        p.min_spacing_m = max(0.0, min(20.0, p.min_spacing_m))
        p.max_distance_m = max(1.0, min(50.0, p.max_distance_m))
    plan.items.sort(key=lambda i: (i.role != "hero", -i.placement.height_m * min(i.placement.count, 3)))
    if settings.only_3d:
        plan.items = [i for i in (_solid(i) for i in plan.items) if i is not None]
    for item in plan.items:
        item.asset_prompt = _single(item.asset_prompt)
        # only things that really hang go on the ceiling (seen: coffee cups and vases upside down there)
        if item.placement.surface == Surface.ceiling and not _HANGS.search(item.asset_prompt + " " + item.label):
            item.placement.surface = Surface.horizontal
        item.behaviour = clean_behaviour(item.behaviour)
        if item.placement.surface == Surface.hand:
            # one small thing that stays on the hand (it would walk or fly away otherwise)
            p = item.placement
            p.count, p.layout, p.target_id = 1, Layout.single, None
            p.height_m = min(p.height_m, 0.3)
            p.footprint_m = min(p.footprint_m, 0.3)
            for st in (item.behaviour.states if item.behaviour else []):
                st.move = "none"
        if item.placement.layout in (Layout.coat, Layout.drape):
            _coat(item)
        # things that move need a known front: generated in side view, front to the right - animals and
        # vehicles always, also while they stand still: a later "let them fly around" then works (seen:
        # birds added still, then set flying - drawn from below with spread wings, they flew backwards)
        if ((_moves(item.behaviour) or could_move(item)) and "facing right" not in item.asset_prompt.lower()
                and item.placement.layout not in (Layout.coat, Layout.drape)):
            item.asset_prompt = (item.asset_prompt.rstrip(". ") + ", side view, facing right")[:300]
    for e in plan.effects:
        normalise_effect(e)
        e.strength = max(0.0, min(1.0, e.strength))
        if e.water_height_m is not None:
            e.water_height_m = max(0.06, min(5.0, e.water_height_m))  # above sand patches and floor paint
        if e.tint_hex is not None:
            e.tint_hex = clean_hex(e.tint_hex)
    plan.effects = [e for e in plan.effects if e.type != EffectType.surface or e.tint_hex or e.colors]
    return plan


PAINT_KINDS = ("wall", "floor", "ceiling", "table", "seat")


def clean_hex(value: str | None) -> str | None:
    """'#3050C0' / '3050c0' / '#35c' -> '#3050c0'; anything else -> None."""
    v = (value or "").strip().lstrip("#").lower()
    if re.fullmatch(r"[0-9a-f]{3}", v):
        v = "".join(c * 2 for c in v)
    return "#" + v if re.fullmatch(r"[0-9a-f]{6}", v) else None


def resolve_surfaces(names: list[str], req: PlanRequest) -> list[str]:
    """S-numbers -> headset plane ids; kinds stay kinds (the headset paints every surface of that
    kind). Unknown names are dropped."""
    to_plane = dict(zip(short_ids(req), (p.id for p in req.scene.planes)))
    out: list[str] = []
    for n in names:
        n = n.strip()
        kind = n.lower().rstrip("s")  # "walls" -> "wall"
        if n.upper() in to_plane:
            out.append(to_plane[n.upper()])
        elif kind in PAINT_KINDS:
            out.append(kind)
    return list(dict.fromkeys(out))


def _paintable(names: list[str], req: PlanRequest) -> bool:
    ids = {p.id for p in req.scene.planes}
    labels = {p.label for p in req.scene.planes}
    return any(n in ids or n in labels for n in names)


_GERMAN = re.compile(r"[äöüß]|\b(die|der|das|und|wäre|waere|wenn|hätte|haette)\b")


# Words that ask for a colour or paint (EN / DE). Without one, wall paint is a guess at a
# material ("medieval" -> grey walls) and only shows as a flat plate: dropped.
_COLOUR_WORDS = re.compile(
    r"paint|colou?r|tint|shade|streich|farb|anmal|lackier|"
    r"\b(red|blue|green|yellow|orange|purple|violet|pink|black|white|gr[ae]y|brown|beige|gold|silver|"
    r"turquoise|teal|rot|blau|gr(ü|ue)n|gelb|lila|rosa|pink|schwarz|wei(ß|ss)|grau|braun|golden|silbern|"
    r"t(ü|ue)rkis|dunkl|hell|dark|light|bright)\w*",
    re.IGNORECASE,
)


# A tint veils the whole view (on the headset it dims the real room): only for wishes about light or
# mood. Seen: a dark green "tint" for jungle walls that turned the floor into a grey veil.
_MOOD_WORDS = re.compile(
    r"night|dark|dusk|dawn|evening|sunset|sunrise|storm|thunder|fog|mist|underwater|under water|gloom|"
    r"light|moon|twilight|nacht|dunkel|d(ä|ae)mmer|abend|sonnenuntergang|sonnenaufgang|sturm|gewitter|"
    r"nebel|unterwasser|unter wasser|licht|mond",
    re.IGNORECASE,
)


# words of a wish about the room's own surfaces or their material (the floor is lava, frozen floor)
_SURFACE_WISH = re.compile(
    r"\b(floor|ground|walls?|ceiling|surfaces?|boden|fu(ß|ss)boden|w(a|ä)nde?|decke|lava|magma|ice|icy|frozen|"
    r"snow|snowy|sand|water|flood|mud|glass|marble|gold|eis|gefroren|schnee|wasser|überflut|ueberflut|"
    r"schlamm|glas|marmor)",
    re.IGNORECASE,
)
# Places whose ground is what makes them recognisable: their floor look is kept (on the floor only)
_GROUND_THEME = re.compile(
    r"\b(deserts?|w(ü|ue)ste[n]?|sahara|gobi|dunes?|d(ü|ue)nen?|beach(es)?|str(a|ä)nde?|sandy|sandig|"
    r"snowfields?|schneefeld|arctic|antarctic|arktis|antarktis|polar|tundra|glaciers?|gletscher|"
    r"sea ?floor|ocean floor|seabed|meeresgrund|meeresboden|underwater|unter wasser|"
    r"moon|mond|mars|volcano|vulkan|swamp|sumpf|marsh)\b",
    re.IGNORECASE,
)
_FLOATING = re.compile(r"firefl|glühwürm|gluehwuerm|dust|staub|glitter|magic|magisch|zauber|spark|funk", re.IGNORECASE)


def _resolve_paint(plan: ScenePlan, req: PlanRequest) -> ScenePlan:
    kept, missing = [], 0
    asked_for_colour = bool(_COLOUR_WORDS.search(req.prompt))
    mood = asked_for_colour or bool(_MOOD_WORDS.search(req.prompt))
    about_surfaces = asked_for_colour or bool(_SURFACE_WISH.search(req.prompt))
    ground_theme = bool(_GROUND_THEME.search(req.prompt))
    floor_coat = any(i.placement.layout == Layout.coat and i.placement.surface in (Surface.floor, Surface.horizontal)
                     for i in plan.items)
    for e in plan.effects:
        if e.type == EffectType.surface and not about_surfaces and ground_theme and floor_coat:
            # the coat already is the ground (sand patches): a see-through floor look between them only
            # greyed the real floor (seen in a desert)
            log.info("dropping the floor look %s: a coat covers the floor", e.tint_hex or e.colors)
            continue
        if e.type == EffectType.surface and not about_surfaces and ground_theme:
            # a desert is above all sandy ground (seen: a desert without any): its floor look stays -
            # on the floor only, walls and ceiling keep their real look
            e.surfaces = ["floor"]  # every floor surface; walls and ceiling stay real
            log.info("keeping the floor look of a place defined by its ground: %s", e.tint_hex or e.colors)
        elif e.type == EffectType.surface and not about_surfaces:
            # seen: a pale "forest floor" over every detected floor plane for a jungle
            log.info("dropping surface effect on %s: the wish is not about the room's surfaces", e.surfaces)
            continue
        # particles in the whole room are tiring: only real emitters, and only a few
        if e.particles == "float" and not _FLOATING.search(req.prompt):
            e.particles = "none"
        e.particle_amount = min(e.particle_amount, 0.25)
        if e.type == EffectType.tint:
            if not settings.tint:
                log.info("dropping tint %s (WHATIF_TINT=0)", e.tint_hex or e.colors)
                continue
            if not mood:
                log.info("dropping tint %s: the prompt asks for no light or mood", e.tint_hex or e.colors)
                continue
            if e.tint_hex is None and e.colors:
                e.tint_hex = clean_hex(e.colors[0])
            e.strength = min(e.strength, 0.6)
        if e.type != EffectType.surface:
            kept.append(e)
            continue
        plain = (e.pattern == "none" and e.motion == "still" and e.glow <= 0.05 and e.particles == "none"
                 and e.edge == "none")
        if plain and not asked_for_colour:
            log.info("dropping surface paint %s on %s: the prompt asks for no colour", e.tint_hex, e.surfaces)
            if _CLAIMS[EffectType.surface].search(plan.note_for_user or ""):
                plan.note_for_user = ""  # it would claim paint that isn't there
            continue
        e.surfaces = resolve_surfaces(e.surfaces or ["wall"], req)
        if e.surfaces and _paintable(e.surfaces, req):
            kept.append(e)
        else:
            missing += 1
    plan.effects = kept
    if missing:
        hint = ("Schau dir die Flächen an, die gestrichen werden sollen, damit die Brille sie erkennt."
                if _GERMAN.search(req.prompt.lower()) and not req.english_only
                else "Look at the surfaces to paint so the headset can detect them, then try again.")
        if hint not in (plan.note_for_user or ""):
            plan.note_for_user = (plan.note_for_user + " " if plan.note_for_user else "") + hint
    return plan


class PlannerError(RuntimeError):
    pass


_QUIET_LOOP = re.compile(r"\b(pant\w*|breath\w*|purr\w*|snor\w*|sniff\w*|heartbeat|soft|quiet|gentle|faint|subtle|"
                         r"hechel\w*|atm\w*|schnurr\w*|schnarch\w*|leise)\b", re.IGNORECASE)


def plan_scene(req: PlanRequest, provider: str | None = None, on_item=None) -> ScenePlan:
    """`provider` overrides WHATIF_PLANNER (claude / local / mock), e.g. to compare planners.
    `on_item(item, interpretation)`: called with each item as soon as the local planner has written it
    (already cleaned up like the final plan), so its generation can start while the rest is written.
    The final plan is returned as usual and contains these items again."""
    provider = provider or settings.planner
    if req.natural and provider == "claude" and not settings.natural_requests_online:
        # Sentences without "What if" may be anyone's conversation: they are only judged by a local
        # planner, never sent online (WHATIF_NATURAL_ONLINE=1 allows it).
        return _not_a_request()
    if req.natural and provider == "mock":
        return _not_a_request()  # the keyword rules can't tell a request from chatter
    early = _EarlyItems(req, on_item) if on_item is not None and provider == "local" else None
    plan = mock_plan(req) if provider == "mock" else _model_plan(req, provider, early)
    if not plan.request:
        return _not_a_request()
    # texts are checked before the paint step, which adds its own hints ("look at the walls to paint them")
    plan = _resolve_paint(_check_texts(_resolve_targets(_enforce_surfaces(_sanitize(plan), req), req)), req)
    plan.note_for_user = _user_text(plan.note_for_user)
    for item in plan.items:
        if item.sound and _QUIET_LOOP.search(item.sound):
            # quiet body sounds come out of the sound model as a hiss (seen: a dog's "soft panting" loop)
            log.info("dropping sound %r of %s: a quiet loop sounds like noise", item.sound, item.id)
            item.sound = ""
    if plan.scope != "environment" and plan.ambience:
        # a background sound belongs to a whole place (a jungle, a beach), not to single objects (seen: a
        # dog with an ambience that made no sense); objects keep their own sounds
        log.info("dropping ambience %r: not an environment", plan.ambience)
        plan.ambience = ""
    plan.interpretation = _user_text(plan.interpretation) or plan.interpretation
    return plan


# Words that claim an effect in the HUD texts, per effect type (EN / DE).
_CLAIMS = {
    EffectType.surface: re.compile(r"paint|streich|anmal", re.IGNORECASE),
    EffectType.level: re.compile(r"water|flood|wasser|überflut|ueberflut|flut", re.IGNORECASE),
}


def _check_texts(plan: ScenePlan) -> ScenePlan:
    """The HUD shows `interpretation` and `note_for_user`: a sentence that claims an effect the plan
    doesn't contain (seen: "walls are painted" with only a tint) is dropped rather than shown."""
    present = {e.type for e in plan.effects}
    for kind, words in _CLAIMS.items():
        if kind in present:
            continue
        if words.search(plan.note_for_user or ""):
            log.info("dropping note that claims %s: %r", kind.value, plan.note_for_user)
            plan.note_for_user = ""
        if words.search(plan.interpretation or "") and plan.items:
            names = ", ".join(i.label for i in plan.items[:3])
            log.info("replacing interpretation that claims %s: %r", kind.value, plan.interpretation)
            plan.interpretation = names[:1].upper() + names[1:] + "."
    return plan


def _not_a_request() -> ScenePlan:
    return ScenePlan(request=False, interpretation="", realism="realistic", items=[], effects=[], note_for_user="")


# --------------------------------------------------------------------------- language models


def _model_plan(req: PlanRequest, provider: str, early=None) -> ScenePlan:
    content: list[dict] = []
    if req.image_jpeg_b64:
        image = mark_surfaces(req)
        content.append({"type": "image", "source": {"type": "base64", "media_type": "image/jpeg", "data": image}})
    content.append({"type": "text", "text": _scene_text(req)})
    if early is not None:
        return local_parse(system_prompt(), content, ScenePlan, on_text=early.feed)
    return structured_call(system_prompt(), content, ScenePlan, settings.planner_effort, provider)


class _ItemScanner:
    """Finds the finished entries of the top-level "items" list in a JSON answer that is still being
    written: each is handed out once its closing brace has arrived."""

    def __init__(self) -> None:
        self.pos, self.depth, self.in_str, self.esc = 0, 0, False, False
        self.str_start, self.last_key, self.in_items, self.obj_start = 0, "", False, -1

    def scan(self, text: str) -> list[str]:
        out: list[str] = []
        i = self.pos
        while i < len(text):
            c = text[i]
            if self.in_str:
                if self.esc:
                    self.esc = False
                elif c == "\\":
                    self.esc = True
                elif c == '"':
                    self.in_str = False
                    if self.depth == 1:
                        self.last_key = text[self.str_start:i]
            elif c == '"':
                self.in_str, self.str_start = True, i + 1
            elif c in "{[":
                if c == "[" and self.depth == 1 and self.last_key == "items":
                    self.in_items = True
                elif c == "{" and self.in_items and self.depth == 2:
                    self.obj_start = i
                self.depth += 1
            elif c in "}]":
                self.depth -= 1
                if c == "}" and self.in_items and self.depth == 2 and self.obj_start >= 0:
                    out.append(text[self.obj_start:i + 1])
                    self.obj_start = -1
                elif c == "]" and self.in_items and self.depth == 1:
                    self.in_items = False
            i += 1
        self.pos = i
        return out


def _top_string(text: str, key: str) -> str | None:
    m = re.search(r'"' + key + r'"\s*:\s*"((?:[^"\\]|\\.)*)"', text)
    if not m:
        return None
    try:
        return json.loads(f'"{m.group(1)}"')
    except ValueError:
        return m.group(1)


class _EarlyItems:
    """Turns the planner's answer, while it is being written, into finished items: each new entry of
    "items" goes through the same clean-up as the final plan (ids, limits, surfaces, targets) together
    with the ones before it, and is handed on once. An item whose target is a later item waits for the
    final plan (its target isn't known yet)."""

    _OWN = re.compile(r"^\s*(|S\d+|IMG.*|GAZE|THERE)\s*$", re.IGNORECASE)

    def __init__(self, req: PlanRequest, on_item) -> None:
        self.req, self.on_item = req, on_item
        self.scanner, self.raw, self.sent = _ItemScanner(), [], set()

    def feed(self, text: str) -> None:
        new = self.scanner.scan(text)
        if not new:
            return
        for chunk in new:
            try:
                self.raw.append(AssetItem.model_validate_json(chunk))
            except Exception:  # noqa: BLE001 - an odd entry is left for the final plan
                self.raw.append(None)
        items = [x for x in self.raw if x is not None]
        if not items:
            return
        scope = _top_string(text, "scope") or "single"
        interpretation = _user_text(_top_string(text, "interpretation") or "") or ""
        known: set[str] = set()
        waiting: set[int] = set()
        for k, it in enumerate(items):
            t = it.placement.target_id or ""
            if not self._OWN.match(t) and t not in known:
                waiting.add(k)
            known.add(it.id)
        partial = ScenePlan(request=True, scope=scope if scope in ("single", "environment") else "single",
                            interpretation=interpretation, realism="realistic",
                            items=[it.model_copy(deep=True) for it in items], coats=[], effects=[], note_for_user="")
        level = log.level
        log.setLevel(logging.WARNING)  # the clean-up runs again for every new item: log it once, at the end
        try:
            cleaned = _resolve_targets(_enforce_surfaces(_sanitize(partial), self.req), self.req).items
        except Exception as e:  # noqa: BLE001 - early items are an optimisation, never a failure
            log.setLevel(level)
            log.warning("early items: %s", e)
            return
        finally:
            log.setLevel(level)
        held = {_sanitized_id(items[k].id) for k in waiting}
        for it in cleaned:
            if it.id in self.sent or it.id in held:
                continue
            self.sent.add(it.id)
            log.info("early item %s %r (%d of the plan so far)", it.id, it.label, len(items))
            try:
                self.on_item(it, interpretation)
            except Exception as e:  # noqa: BLE001
                log.warning("early item %s: %s", it.id, e)


def _sanitized_id(raw: str) -> str:
    return re.sub(r"[^a-z0-9_]+", "_", raw.lower()).strip("_") or "item"


def structured_call(system: str, content: list[dict], output_format, effort: str, provider: str | None = None):
    """One structured answer from the configured model (shared by the planner and the editor).
    `content` uses Anthropic's block format (text / base64 image); converted for the local model."""
    provider = provider or settings.planner
    if provider == "local":
        return local_parse(system, content, output_format)
    return claude_parse(system, content, output_format, effort)


def _bounded(schema: dict, max_items: int = 16, max_chars: int = 400) -> dict:
    """Hard limits for the local model's forced JSON: every list at most `max_items` entries, every
    free text at most `max_chars` characters. Local models sometimes loop inside a list or a string
    (seen: a plan running to 7800 tokens until it was cut off); vLLM enforces these while generating."""
    def walk(node):
        if isinstance(node, dict):
            if node.get("type") == "array" and "maxItems" not in node:
                node["maxItems"] = max_items
            if node.get("type") == "string" and "enum" not in node and "maxLength" not in node:
                node["maxLength"] = max_chars
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)
    walk(schema)
    return schema


def _answer_room(system: str, parts: list[dict]) -> int:
    """Answer tokens to ask for: the context minus a generous estimate of the question (~3 characters
    per token for the schema-heavy prompt, ~1500 per camera picture), between 1200 and 8000."""
    chars = len(system) + sum(len(p.get("text", "")) for p in parts) + 6000  # + the JSON schema
    pictures = sum(1 for p in parts if p.get("type") == "image_url")
    question = chars // 3 + 1500 * pictures
    return max(1200, min(8000, settings.local_context - question - 200))


def local_parse(system: str, content: list[dict], output_format, on_text=None):
    """One structured-output call to the local model (vLLM, OpenAI-compatible API). vLLM's guided
    decoding forces the answer into the JSON schema; the schema also goes into the prompt, so the
    model knows what each field means (Claude gets it through the API).
    `on_text(answer_so_far)`: the answer is streamed and handed on while it is written (for plans:
    objects start generating before the whole plan is done). Any problem: the normal call below."""
    import time

    import httpx
    from pydantic import ValidationError

    schema = _bounded(output_format.model_json_schema())
    parts: list[dict] = []
    for block in content:
        if block.get("type") == "image":
            src = block["source"]
            parts.append({"type": "image_url", "image_url": {"url": f"data:{src['media_type']};base64,{src['data']}"}})
        elif block.get("type") == "text":
            parts.append({"type": "text", "text": block["text"]})
    body = {
        "model": settings.local_model,
        "messages": [
            {"role": "system", "content": system + "\n\nAnswer with one JSON object that follows this JSON "
                                                   "schema (field descriptions explain each value):\n"
                                                   + json.dumps(schema, ensure_ascii=False)},
            {"role": "user", "content": parts},
        ],
        "temperature": settings.local_temperature,
        # room for the answer: what the context leaves after the (estimated) question, at most 8000.
        # Fixed 8000 made vLLM refuse big edits (400); fixed 3000 cut off big scenes' plans.
        "max_tokens": _answer_room(system, parts),
        "response_format": {"type": "json_schema",
                            "json_schema": {"name": output_format.__name__, "schema": schema, "strict": True}},
        "chat_template_kwargs": {"enable_thinking": False},  # Qwen3.x: answer directly, no reasoning text
    }
    t = time.perf_counter()
    url = settings.local_url.rstrip("/") + "/chat/completions"
    if on_text is not None:
        streamed = _local_stream(url, body, on_text, t)
        if streamed is not None:
            try:
                return output_format.model_validate_json(streamed)
            except ValidationError:
                log.warning("local planner: streamed answer not usable, asking again without streaming")
    try:
        r = httpx.post(url, json=body, timeout=settings.local_timeout_s)
        if r.status_code == 400 and ("maxItems" in r.text or "maxLength" in r.text or "schema" in r.text.lower()):
            # a vLLM without support for the list / text limits: once more without them
            log.warning("local planner: schema limits not accepted, retrying without them: %s", r.text[:200])
            plain = output_format.model_json_schema()
            body["response_format"]["json_schema"]["schema"] = plain
            body["messages"][0]["content"] = body["messages"][0]["content"].replace(
                json.dumps(schema, ensure_ascii=False), json.dumps(plain, ensure_ascii=False))
            r = httpx.post(url, json=body, timeout=settings.local_timeout_s)
        if r.status_code == 400 and ("context" in r.text or "max_tokens" in r.text or "too long" in r.text):
            log.warning("local planner: prompt too long for %d answer tokens, retrying with 1200", body["max_tokens"])
            body["max_tokens"] = 1200
            r = httpx.post(url, json=body, timeout=settings.local_timeout_s)
    except httpx.HTTPError as e:
        raise PlannerError(f"Cannot reach the local planner at {settings.local_url} - is vLLM running?") from e
    if r.status_code != 200:
        log.warning("local planner error %d: %s", r.status_code, r.text[:500])
        raise PlannerError(f"Local planner error {r.status_code}: {r.text[:300]}")
    data = r.json()
    choice = data["choices"][0]
    text = choice["message"].get("content") or ""
    usage = data.get("usage", {})
    log.info("local planner: %d prompt + %d answer tokens in %.1f s", usage.get("prompt_tokens", 0),
             usage.get("completion_tokens", 0), time.perf_counter() - t)
    if choice.get("finish_reason") == "length":
        # cut off: once more with all the room the context leaves (the question's real size is known now)
        room = settings.local_context - int(usage.get("prompt_tokens", 0)) - 64
        if room > body["max_tokens"] + 500:
            log.warning("local planner: answer cut off at %d tokens, retrying with %d", body["max_tokens"], room)
            body["max_tokens"] = room
            try:
                r = httpx.post(url, json=body, timeout=settings.local_timeout_s * 2)
            except httpx.HTTPError as e:
                raise PlannerError(f"Cannot reach the local planner at {settings.local_url}") from e
            if r.status_code == 200:
                data = r.json()
                choice = data["choices"][0]
                text = choice["message"].get("content") or ""
    if choice.get("finish_reason") == "length":
        raise PlannerError("The local planner's answer was cut off")
    try:
        return output_format.model_validate_json(text)
    except ValidationError as e:
        raise PlannerError("No usable answer from the local planner") from e


def _local_stream(url: str, body: dict, on_text, t0: float) -> str | None:
    """The answer streamed (server-sent events), `on_text` called whenever it grew; None if streaming
    didn't work or the answer was cut off (the caller then asks the normal way, with its retries)."""
    import time

    import httpx

    body = dict(body, stream=True, stream_options={"include_usage": True})
    text, finish, usage, first = "", None, {}, None
    try:
        with httpx.stream("POST", url, json=body, timeout=settings.local_timeout_s) as r:
            if r.status_code != 200:
                r.read()
                log.warning("local planner: streaming refused (%d), asking the normal way", r.status_code)
                return None
            for line in r.iter_lines():
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if data == "[DONE]":
                    break
                try:
                    chunk = json.loads(data)
                except ValueError:
                    continue
                usage = chunk.get("usage") or usage
                for choice in chunk.get("choices") or []:
                    piece = (choice.get("delta") or {}).get("content") or ""
                    if piece:
                        if first is None:
                            first = time.perf_counter() - t0
                        text += piece
                        if "}" in piece:
                            on_text(text)
                    finish = choice.get("finish_reason") or finish
    except httpx.HTTPError as e:
        log.warning("local planner: streaming failed (%s), asking the normal way", e)
        return None
    log.info("local planner (streamed): %d prompt + %d answer tokens in %.1f s, first after %.1f s",
             usage.get("prompt_tokens", 0), usage.get("completion_tokens", 0), time.perf_counter() - t0, first or 0.0)
    if finish == "length":
        log.warning("local planner: streamed answer cut off, asking again with more room")
        return None
    return text


def local_planner_reachable() -> bool:
    """For /health: does vLLM answer (short timeout)?"""
    import httpx

    try:
        return httpx.get(settings.local_url.rstrip("/") + "/models", timeout=1.5).status_code == 200
    except httpx.HTTPError:
        return False


def claude_parse(system: str, content: list[dict], output_format, effort: str):
    """One structured-output call to Claude (shared by the planner and the editor)."""
    import anthropic

    client = anthropic.Anthropic()
    extra: dict = {}
    if settings.planner_fallbacks:
        # Server-side refusal fallback: a declined request is re-run on Anthropic's
        # recommended fallback model instead of failing.
        extra = {
            "extra_headers": {"anthropic-beta": "server-side-fallback-2026-07-01"},
            "extra_body": {"fallbacks": "default"},
        }
    try:
        response = client.messages.parse(
            model=settings.planner_model,
            max_tokens=16000,
            system=system,
            messages=[{"role": "user", "content": content}],
            output_config={"effort": effort},
            output_format=output_format,
            **extra,
        )
    except anthropic.AuthenticationError as e:
        raise PlannerError("Anthropic credentials missing or invalid (set ANTHROPIC_API_KEY)") from e
    except anthropic.RateLimitError as e:
        raise PlannerError("Planner rate-limited, try again shortly") from e
    except anthropic.APIStatusError as e:
        raise PlannerError(f"Planner API error {e.status_code}: {e.message}") from e
    except anthropic.APIConnectionError as e:
        raise PlannerError("Cannot reach the Anthropic API") from e

    if response.stop_reason == "refusal":
        raise PlannerError("The request was declined")
    if response.stop_reason == "max_tokens" or response.parsed_output is None:
        raise PlannerError("No usable answer from Claude")
    return response.parsed_output


# --------------------------------------------------------------------------- offline mock


def _item(id_, label, prompt, surface, layout, count, h, spacing, want_3d=True, dist=15.0,
          facing=Facing.random, footprint=None) -> AssetItem:
    return AssetItem(
        id=id_,
        label=label,
        asset_prompt=prompt,
        placement=Placement(
            surface=surface,
            layout=layout,
            facing=facing,
            count=count,
            height_m=h,
            footprint_m=footprint if footprint is not None else h * 0.8,
            height_jitter=0.2,
            min_spacing_m=spacing,
            max_distance_m=dist,
        ),
        want_3d=want_3d,
    )


def mock_plan(req: PlanRequest) -> ScenePlan:
    """Keyword-based planner so the full pipeline can be tested without an API key."""
    p = req.prompt.lower()
    indoor = any(pl.label == "ceiling" for pl in req.scene.planes)
    tree_h = 2.2 if indoor else 6.0
    items: list[AssetItem] = []
    effects: list[Effect] = []
    realism = "realistic"
    note = "Offline mock planner - set WHATIF_PLANNER=claude for real planning."

    if re.search(r"plant|green|tree|garden|pflanz|baum|grün", p):
        items += [
            _item("tree", "tree", "a young linden tree with a full green crown, realistic photo",
                  Surface.floor, Layout.edge, 4, tree_h, 3.0),
            _item("flower_bed", "flowering shrub", "a lush flowering shrub with purple and white blossoms, realistic photo",
                  Surface.horizontal, Layout.scatter, 8, 0.8, 1.0),
            _item("ivy", "climbing ivy", "a dense patch of climbing ivy leaves, realistic photo",
                  Surface.wall, Layout.scatter, 3, 1.5, 1.5, want_3d=False),
        ]
    if re.search(r"car|auto|traffic|verkehr", p):
        items += [
            _item("bike_rack", "bicycle with rack", "a city bicycle parked in a metal bike rack, realistic photo",
                  Surface.floor, Layout.row, 5, 1.1, 1.0, want_3d=False),
            _item("bench", "wooden bench", "a modern wooden park bench, realistic photo",
                  Surface.floor, Layout.edge, 2, 0.9, 3.0, facing=Facing.back_to_wall),
        ]
        note = "Real cars cannot be removed yet; showing what could replace them."
    if re.search(r"flood|water|überflut|hochwasser|wasser", p):
        effects.append(Effect(type=EffectType.water_level, water_height_m=0.6, strength=0.6))
        items.append(_item("buoy", "life buoy", "an orange life buoy ring floating, realistic photo",
                           Surface.floor, Layout.scatter, 3, 0.15, 2.0, want_3d=False))
    if m := re.search(r"\b(blue|red|green|yellow|white|blau|rot|grün|gelb|weiß)\w*\b.*\b(wall|wand|wände|floor|boden)", p):
        colours = {"blue": "#3050c0", "blau": "#3050c0", "red": "#b03030", "rot": "#b03030", "green": "#3a8a40",
                   "grün": "#3a8a40", "yellow": "#e0c040", "gelb": "#e0c040", "white": "#f0f0f0", "weiß": "#f0f0f0"}
        kind = "floor" if m.group(2) in ("floor", "boden") else "wall"
        effects.append(Effect(type=EffectType.surface_paint, tint_hex=colours[m.group(1)], strength=0.8, surfaces=[kind]))
    if re.search(r"dinner|dining|chair|stuhl|tisch", p):
        tables = [pl for pl in req.scene.planes if pl.label == "table"]
        if not tables:
            items.append(_item("dining_table", "dining table", "a round wooden dining table, realistic photo",
                               Surface.floor, Layout.single, 1, 0.75, 2.0, footprint=1.2))
        chairs = _item("chair", "dining chair", "a wooden dining chair, realistic photo",
                       Surface.floor, Layout.around, 4, 0.9, 0.6, facing=Facing.toward_target, footprint=0.5)
        chairs.placement.target_id = "S" + str(req.scene.planes.index(tables[0]) + 1) if tables else "dining_table"
        items.append(chairs)
    if re.search(r"dino", p):
        realism = "playful"
        items += [
            _item("brachiosaurus", "brachiosaurus", "a friendly brachiosaurus dinosaur, full body side view, realistic render",
                  Surface.floor, Layout.single, 1, 6.0 if not indoor else 2.4, 5.0, facing=Facing.toward_user),
            _item("raptor", "small raptor", "a small feathered raptor dinosaur standing, full body, realistic render",
                  Surface.floor, Layout.scatter, 3, 1.0, 2.0, facing=Facing.toward_user),
        ]
    if not items and not effects:
        items.append(_item("object", req.prompt[:40], f"{req.prompt}, single isolated object, realistic photo",
                           Surface.horizontal, Layout.single, 1, 1.0, 1.0))
    return ScenePlan(
        interpretation=f'Visualising "{req.prompt}" around you.',
        realism=realism,
        items=items,
        effects=effects,
        note_for_user=note,
    )


def dump_schema() -> str:
    return json.dumps(ScenePlan.model_json_schema(), indent=2)
