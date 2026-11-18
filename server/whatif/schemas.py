"""Data contracts shared between the Magic Leap client and the generation server.

Coordinates are Unity world space (left-handed, Y up, metres). The server never
needs exact coordinates for generation; it only uses them to give the planner a
sense of scale ("there is a 6 m x 3 m floor area 2 m in front of you").
"""

from __future__ import annotations

from enum import Enum
from typing import Literal

from pydantic import BaseModel, Field


# --------------------------------------------------------------------------- scene


class PlaneInfo(BaseModel):
    """One surface detected by the headset's plane extraction."""

    id: str
    label: Literal["floor", "wall", "ceiling", "table", "seat", "unknown"] = "unknown"
    width_m: float
    height_m: float
    distance_m: float = Field(description="Horizontal distance from the user's head to the plane centre")
    height_rel_head_m: float = Field(description="Plane centre height relative to the head (negative = below)")
    bearing_deg: float | None = Field(default=None, description="Direction from the user's gaze, + = right")
    height_above_floor_m: float | None = None
    # Where the plane centre appears in the camera image (0..1 from the top left), if it is in view.
    # The server draws the surface's number there, so the planner can link the photo to the list.
    view_x: float | None = None
    view_y: float | None = None


class SceneContext(BaseModel):
    planes: list[PlaneInfo] = Field(default_factory=list)
    head_height_m: float | None = Field(default=None, description="Estimated head height above the floor")
    mesh_extent_m: float | None = Field(default=None, description="Rough radius of the reconstructed mesh")


class PlanRequest(BaseModel):
    prompt: str = Field(min_length=1, max_length=500)
    english_only: bool = Field(default=False, description="Write every text shown to the user in English, whatever language they speak")
    scene: SceneContext = Field(default_factory=SceneContext)
    image_jpeg_b64: str | None = Field(default=None, description="Optional camera frame of the user's view")
    generate: bool = Field(default=True, description="Start asset generation for every item right away")
    looked_at: str | None = Field(default=None, description="Where the user looked while speaking, in words "
                                                            "(target GAZE = that point)")
    natural: bool = Field(default=False, description="Said without 'What if' while nothing is shown: may be talk "
                                                     "to other people; the planner decides whether it is a request")


# --------------------------------------------------------------------------- plan


class Surface(str, Enum):
    floor = "floor"
    wall = "wall"
    ceiling = "ceiling"
    table = "table"
    horizontal = "horizontal"  # floor or table, whichever exists
    any = "any"
    hand = "hand"  # the user's hand: a bird or a small thing sits on it and follows it


class Layout(str, Enum):
    scatter = "scatter"  # random, spacing respected
    row = "row"  # evenly along the long axis of a surface
    edge = "edge"  # along the boundary of a surface (curbs, railings, walls)
    single = "single"  # one hero object in front of the user
    around = "around"  # in a ring around `target_id` (chairs around a table), on the floor
    beside = "beside"  # on the floor next to `target_id`, on the side towards the user
    under = "under"  # on the floor beneath `target_id` (a table, a seat), sized to fit below it
    coat = "coat"  # many small pieces covering a surface (moss on the floor and table tops, leaves, sand, snow)
    drape = "drape"  # hanging down over the edges of real tables and seats (ivy, seaweed, a cloth, icicles)


class Facing(str, Enum):
    random = "random"  # no front: plants, trees, rocks, round things
    toward_user = "toward_user"  # animals, people, statues, signs, vehicles
    back_to_wall = "back_to_wall"  # sofas, shelves, benches, cabinets: back against the nearest wall
    toward_target = "toward_target"  # face `target_id` (chairs towards their table)


class Placement(BaseModel):
    surface: Surface
    layout: Layout
    facing: Facing
    target_id: str | None = Field(
        default=None,
        description="A detected surface (S1, S2, ...) or the id of another item in this plan that this "
        "item relates to: required for layouts around/beside and facing toward_target; with other "
        "layouts it restricts placement to that one surface (e.g. 'on this table').",
    )
    count: int = Field(description="How many instances, 1-60 (with density set: a rough guess, the headset "
                                   "works out the real number from the free area)")
    height_m: float = Field(description="Typical real-world height of one instance in metres")
    footprint_m: float = Field(
        description="Largest horizontal size of one instance in metres, used to keep objects from "
        "overlapping (a 3-seat sofa ~2.2, an armchair ~0.9, a chair ~0.5, a mature tree's crown ~5)"
    )
    height_jitter: float = Field(description="Relative size variation 0-0.5, e.g. 0.2 = +/-20 %")
    min_spacing_m: float = Field(description="Minimum distance between instances")
    max_distance_m: float = Field(description="Do not place further than this from the user")
    density: Literal["", "sparse", "normal", "dense", "very_dense"] = Field(
        default="", description="For fillers spread over a surface (ferns, stones, books): how much of the free "
        "area they cover; the headset computes the count from the room. Empty = use count as given")


class Transition(BaseModel):
    event: Literal["user_looks", "user_near", "arrived", "timer"] = Field(
        description="user_looks: the user looks at it; user_near: closer than value metres; arrived: the movement "
        "reached its goal; timer: after value seconds in this state")
    value: float = Field(default=0.0, description="user_near: metres; timer: seconds")
    goto: str = Field(description="Name of the next state")


class BehaviourState(BaseModel):
    name: str
    move: Literal["none", "wander", "to_user", "follow_user", "flee_user", "circle", "to_target"] = Field(
        default="none", description="Where it goes: wander = random free spots; to_user stops ~0.8 m away; "
        "follow_user keeps ~1.2 m; flee_user runs away; circle = around where it started; to_target = to target_id")
    target_id: str | None = Field(default=None, description="For to_target: another item's id")
    gait: Literal["glide", "roll", "hop", "trot", "waddle", "slither", "fly", "swim"] = Field(
        default="glide", description="How it moves: roll (wheels, leans into turns), hop, trot (bouncing), waddle "
        "(rocking), slither (winding), fly (in the air, banking), swim (slow up and down)")
    speed: float = Field(default=0.5, description="Metres per second (a walking dog ~0.8, a car indoors ~1.5)")
    idle: list[Literal["breathe", "sway", "bob", "spin", "look_at_user", "tilt"]] = Field(
        default_factory=list, description="Movement in place: breathe (living things), sway (plants, flames), bob "
        "(floating things), spin (fans, wheels of fortune), look_at_user, tilt (curious head tilt)")
    next: list[Transition] = Field(default_factory=list, description="When to switch to which state")


class Behaviour(BaseModel):
    """A small state machine from building blocks; the headset runs it (nothing is hard-coded per object)."""

    states: list[BehaviourState] = Field(description="1-4 states; the first one is the start")


class AssetItem(BaseModel):
    id: str = Field(description="Short snake_case id, unique within the plan")
    label: str = Field(description="Human readable name, e.g. 'linden tree'")
    asset_prompt: str = Field(
        description="Visual description of ONE isolated object for an image generator, at most 25 words: "
        "subject, species/type, style, colours. No background, no scene, no other objects."
    )
    placement: Placement
    want_3d: bool = Field(description="True if the object benefits from being seen from several sides")
    sound: str = Field(default="", description="Only for a few characteristic objects (at most 3 kinds per plan): "
                       "a short English description of the sound it makes ('old grandfather clock ticking', 'small "
                       "fountain splashing'); empty for most objects")
    sound_loop: bool = Field(default=True, description="True for continuous sounds (ticking, splashing), false for "
                                                       "a short one-off sound when it appears (a bark)")
    parts: list[str] = Field(default_factory=list, description="Parts that should move, as 'part:motion' with "
                             "motion wag / nod / spin / flap / swing ('tail:wag', 'head:nod', 'wheels:spin', "
                             "'wings:flap', 'legs:swing'); only for clearly separate parts of moving things")
    web_search: str = Field(default="", description="Only for one specific real thing whose exact look matters "
                            "(a famous landmark, a specific car model, a famous artwork, a historical figure): a "
                            "short image search query ('Eiffel Tower', 'Johann Wolfgang von Goethe'); empty for "
                            "generic objects")
    web_kind: Literal["thing", "person"] = Field(default="thing", description="person if web_search names a person")
    emit: Literal["none", "rise", "fall", "float"] = Field(
        default="none", description="Only for things that really give off particles: a fire pit, torch or candle "
        "(rise, orange sparks), a steaming pot (rise, white), a fountain (rise, light blue); none for everything else")
    emit_hex: str | None = Field(default=None, description="Colour of the emitted particles")
    behaviour: Behaviour | None = Field(default=None, description="For living things and machines: how it "
                                        "behaves (see the planner rules). None for furniture and still objects")
    role: Literal["hero", "filler"] = Field(
        default="hero", description="hero: a prominent piece people look at (a throne, a big tree, an animal); "
        "filler: one of many small things that fill the space (ferns, stones, books, candles) - gets a lighter "
        "3D model so a full room stays smooth on the headset")


class EffectType(str, Enum):
    surface = "surface"  # a look over detected real surfaces (paint, a lava floor, an ice floor, stars)
    level = "level"  # a horizontal layer at a height above the floor (water, fog, a flood of lava)
    tint = "tint"  # global colour grade / dimming
    water_level = "water_level"  # older name of a level, converted to `level`
    surface_paint = "surface_paint"  # older name of a surface, converted to `surface`


class Effect(BaseModel):
    """A surface look built from blocks (no hard-coded water or lava): where, colours, pattern,
    movement, glow, reflection, edges and particles. `tint` uses only tint_hex and strength."""

    type: EffectType
    water_height_m: float | None = Field(default=None, description="For level: height above the floor in metres")
    tint_hex: str | None = Field(default=None, description="Main colour such as #4060ff (tint, surface, level)")
    strength: float = Field(default=0.5, description="0-1: opacity (0.5 lets the real surface show through, "
                                                     "0.9 covers it); for tint how strong")
    surfaces: list[str] = Field(
        default_factory=list,
        description="For surface: detected surfaces, as S-numbers (S3) or kinds (wall, floor, ceiling, table); "
        "a kind means all detected surfaces of that kind",
    )
    colors: list[str] = Field(default_factory=list, description="Up to 3 more colours the pattern mixes (dark to "
                                                                "bright), e.g. lava: crust #1a0d08, cracks #ff6a00")
    pattern: Literal["none", "noise", "cells", "waves", "stripes", "speckle"] = Field(
        default="none", description="none = plain colour; noise = clouds/mottled; cells = cracks between cells "
        "(lava crust, dry mud, ice); waves = water ripples; stripes; speckle = small dots (stars, glitter)")
    pattern_scale_m: float = Field(default=0.5, description="Size of one pattern element in metres")
    motion: Literal["still", "flow", "ripple", "bubble"] = Field(default="still", description="Movement")
    speed: float = Field(default=0.2, description="Movement speed, 0.05 slow (honey) to 1 fast (rapids)")
    glow: float = Field(default=0.0, description="0-1: how much the bright parts of the pattern glow (lava, stars)")
    glow_hex: str | None = Field(default=None, description="Glow colour")
    reflect: float = Field(default=0.0, description="0-1: mirror-like towards grazing angles (water, ice, honey)")
    edge: Literal["none", "foam", "glow", "frost"] = Field(default="none", description="Where the surface meets "
                                                                                       "real objects and walls")
    edge_hex: str | None = Field(default=None, description="Edge colour")
    particles: Literal["none", "rise", "fall", "float"] = Field(
        default="none", description="rise: embers, bubbles, steam; fall: snow, ash, petals; float: fireflies, dust")
    particle_hex: str | None = Field(default=None, description="Particle colour")
    particle_amount: float = Field(default=0.3, description="0-1: how many particles")


class ScenePlan(BaseModel):
    request: bool = Field(default=True, description="False if the sentence is not a wish about how the surroundings "
                                                    "should look (talk to someone, a comment, a question): then no "
                                                    "items and no effects")
    scope: Literal["single", "environment"] = Field(
        default="single", description="single: the user asks for one object or a few; environment: the whole "
        "place should change (a jungle, a medieval hall, the sea floor)")
    ambience: str = Field(default="", description="For an environment: a short English description of its "
                          "background sound ('jungle: birds, insects, distant monkeys'); empty for single objects")
    interpretation: str = Field(description="One sentence: how the 'what if' is being visualised here")
    realism: Literal["realistic", "playful"]
    items: list[AssetItem]
    coats: list[AssetItem] = Field(
        default_factory=list, description="For an environment: 1-3 coats of the real room (layout coat: small "
        "pieces lying close together on the floor or table tops - moss, leaves, sand, straw, snow; layout drape: "
        "pieces hanging over table and seat edges - ivy, seaweed, cloth). Empty for a single object.")
    effects: list[Effect]
    note_for_user: str = Field(description="Short hint shown in the headset, e.g. what could not be done")

    # `coats` is required in the answer's schema (it has a default for code that builds plans): left
    # optional, the local model skipped it every time - asked for explicitly, it thinks about it
    model_config = {"json_schema_extra": lambda s: s.setdefault("required", []).append("coats")
                    if "coats" not in s.get("required", []) else None}


# --------------------------------------------------------------------------- assets


class AssetStatus(str, Enum):
    queued = "queued"
    running = "running"
    billboard_ready = "billboard_ready"  # 2D cut-out available, 3D still being made
    done = "done"
    failed = "failed"


class AssetRequest(BaseModel):
    prompt: str = Field(min_length=1, max_length=400)
    want_3d: bool = False


class AssetInfo(BaseModel):
    id: str
    prompt: str
    status: AssetStatus
    billboard_url: str | None = None
    billboard_aspect: float | None = Field(default=None, description="width / height of the cut-out")
    model_url: str | None = None
    pending: bool = Field(default=False, description="True while the server is still working on this asset")
    error: str | None = None


class SoundInfo(BaseModel):
    id: str
    prompt: str
    loop: bool = True
    status: Literal["queued", "done", "failed"] = "queued"
    url: str | None = None
    error: str | None = None


class PlanResponse(BaseModel):
    room_light: str | None = Field(default=None, description="Average colour of the camera picture (the room's light), so the headset needn't decode it")
    plan: ScenePlan
    assets: dict[str, AssetInfo] = Field(default_factory=dict, description="item id -> asset")
    sounds: dict[str, SoundInfo] = Field(default_factory=dict, description="item id (or 'ambience') -> sound")


# --------------------------------------------------------------------------- editing by voice


class SceneObject(BaseModel):
    """One placed instance in the headset's scene, as the editor sees it."""

    id: str = Field(description="Instance id, e.g. 'chair#2'")
    group: str = Field(description="Item id the instance belongs to (all chairs of one kind share it)")
    label: str
    asset_prompt: str
    bearing_deg: float = Field(description="Direction from the user's gaze, + = right")
    distance_m: float
    height_m: float
    footprint_m: float
    height_above_floor_m: float = 0.0
    looked_at: bool = False


class EditRequest(BaseModel):
    utterance: str = Field(min_length=1, max_length=500)
    english_only: bool = Field(default=False, description="Write every text shown to the user in English, whatever language they speak")
    scene: SceneContext = Field(default_factory=SceneContext)
    objects: list[SceneObject] = Field(default_factory=list)
    gaze_id: str | None = Field(default=None, description="Object the user looked at while speaking")
    last_ids: list[str] = Field(default_factory=list, description="Objects changed or added most recently")
    scenario: str | None = Field(default=None, description="The current 'what if' scenario")
    effects: list[Effect] = Field(default_factory=list, description="Effects currently shown (water, tint, paint)")
    looked_at: str | None = Field(default=None, description="Where the user looked while speaking, in words "
                                                            "(target GAZE = that point)")
    image_jpeg_b64: str | None = Field(default=None, description="Optional camera frame of the user's view")
    generate: bool = True
    natural: bool = Field(default=False, description="Said while not looking at the scene: may be talk to other "
                                                     "people; the planner decides whether it is meant for the scene")


class EditOpType(str, Enum):
    move = "move"  # by an offset in the user's view: right / up / forward
    move_to = "move_to"  # to a direction + distance from the user, or on / beside / around target_id
    scale = "scale"
    rotate = "rotate"
    tip = "tip"  # lay it down: on its back (180), on its side (90), tipped forward (-90)
    face = "face"
    restyle = "restyle"  # same objects, new look: new asset_prompt (colour, material, kind)
    delete = "delete"
    add = "add"  # new objects, placed like plan items
    paint = "paint"  # colour real surfaces: targets = surface S-numbers or kinds, color_hex
    undo = "undo"  # revert the previous edit
    clear = "clear"  # remove everything: all objects, effects and paint ("delete the whole scene")


class EditOp(BaseModel):
    # Every field is required: optional fields count against the structured-output limits (the API
    # rejected the schema as "too complex"). Unused fields carry neutral values: 0, 1, "", "none".
    type: EditOpType
    targets: list[str] = Field(description="Object ids the operation applies to ([] for add / undo); "
                               "for paint: surface S-numbers or kinds (wall, floor, ceiling, table)")
    dx_right_m: float = Field(description="move: metres to the user's right (- = left); else 0")
    dy_up_m: float = Field(description="move: metres up (- = down); else 0")
    dz_forward_m: float = Field(description="move: metres away from the user (- = closer); else 0")
    bearing_deg: float = Field(description="move_to: direction from the user's gaze, + = right, 180 = behind; else 0")
    distance_m: float = Field(description="move_to: distance from the user; 0 = use target_id instead")
    target_id: str = Field(description="move_to / face / add: object id, item group or surface S-number; else \"\"")
    relation: Literal["none", "on", "beside", "around", "under"] = Field(description="move_to with target_id; else none")
    scale_factor: float = Field(description="scale: e.g. 1.5 = 50 % bigger; else 1")
    rotate_deg: float = Field(description="rotate: degrees clockwise seen from above; tip: 180 on its back, 90 on "
                              "its side, -90 tipped forward; else 0")
    facing: Literal["none", "toward_user", "toward_target", "random"] = Field(description="face; else none")
    asset_prompt: str = Field(description="restyle: full new English description of ONE object; else \"\"")
    label: str = Field(description="restyle: new short name, e.g. 'blue chair'; else \"\"")
    item: AssetItem | None = Field(description="add: the new objects, like a plan item; else null")
    color_hex: str = Field(description="paint: colour such as #3050c0, \"none\" = remove the paint; else \"\"")

    @classmethod
    def make(cls, type: "EditOpType", **fields) -> "EditOp":
        """Build an operation in code, with neutral values for everything not given."""
        neutral = dict(targets=[], dx_right_m=0.0, dy_up_m=0.0, dz_forward_m=0.0, bearing_deg=0.0, distance_m=0.0,
                       target_id="", relation="none", scale_factor=1.0, rotate_deg=0.0, facing="none",
                       asset_prompt="", label="", item=None, color_hex="")
        return cls(type=type, **{**neutral, **fields})


class BehaviourChange(BaseModel):
    targets: list[str] = Field(description="Object ids or groups")
    behaviour: Behaviour


class EditPlan(BaseModel):
    reply: str = Field(description="One short sentence for the headset, in the user's language; empty if nothing to do")
    operations: list[EditOp]
    effects: list[Effect] | None = Field(default=None, description="Only if the sentence changes the effects "
        "beyond a plain colour ('make the lava glow more', 'water higher', 'add fog', 'no more water'): the complete "
        "new list of effects (keep the unchanged ones). null if the effects stay as they are")
    behaviours: list[BehaviourChange] = Field(default_factory=list, description="Only if the sentence changes how "
        "objects behave ('let the dog sit', 'the car should drive faster', 'make the bird circle the lamp'): the "
        "objects and their complete new behaviour")
    new_scene: str = Field(default="", description="Only if the sentence asks for a whole different place or "
                                                   "theme replacing the current one ('what if this were a beach', 'now "
                                                   "make it a jungle'): that wish as a 'What if ...' sentence in the "
                                                   "user's language, and no operations. Adding or changing things "
                                                   "('what if there was a mug on my table') is an edit: empty")


class AppliedOp(EditOp):
    """An operation as returned to the headset, with generation started where needed."""

    asset: AssetInfo | None = None


class EditResponse(BaseModel):
    room_light: str | None = Field(default=None, description="Average colour of the camera picture (the room's light), so the headset needn't decode it")
    reply: str
    operations: list[AppliedOp]
    new_scene: str = ""  # non-empty: the headset starts this as a new scene
    effects: list[Effect] | None = None  # non-null: replaces the effects shown
    behaviours: list[BehaviourChange] = Field(default_factory=list)  # new behaviour per object
