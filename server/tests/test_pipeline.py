"""End-to-end test of the server with offline providers (no API key, no GPU)."""

import os
import time

os.environ["WHATIF_PLANNER"] = "mock"
os.environ["WHATIF_IMAGE_PROVIDER"] = "mock"
os.environ["WHATIF_MESH_PROVIDER"] = "crossquad"
os.environ["WHATIF_REMOVE_BG"] = "0"


def _client(tmp_path):
    os.environ["WHATIF_DATA_DIR"] = str(tmp_path)
    import importlib

    import whatif.config
    import whatif.assets
    import whatif.main

    importlib.reload(whatif.config)
    importlib.reload(whatif.assets)
    importlib.reload(whatif.main)
    from fastapi.testclient import TestClient

    return TestClient(whatif.main.app)


def _wait(client, aid, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        info = client.get(f"/assets/{aid}").json()
        if not info["pending"]:
            return info
        time.sleep(0.1)
    raise TimeoutError(aid)


def test_plan_and_generate(tmp_path):
    import trimesh

    client = _client(tmp_path)
    scene = {
        "planes": [
            {"id": "p1", "label": "floor", "width_m": 8, "height_m": 4, "distance_m": 2, "height_rel_head_m": -1.6},
            {"id": "p2", "label": "wall", "width_m": 5, "height_m": 3, "distance_m": 3, "height_rel_head_m": 0},
        ],
        "head_height_m": 1.6,
    }
    r = client.post("/plan", json={"prompt": "What if this place had more plants?", "scene": scene})
    assert r.status_code == 200, r.text
    body = r.json()
    items = body["plan"]["items"]
    assert {i["id"] for i in items} == set(body["assets"])

    tree = next(i for i in items if i["id"] == "tree")
    info = _wait(client, body["assets"]["tree"]["id"])
    assert info["status"] == "done", info
    assert 0.2 < info["billboard_aspect"] < 3

    png = client.get(info["billboard_url"])
    assert png.headers["content-type"] == "image/png"

    glb = client.get(info["model_url"])
    path = tmp_path / "t.glb"
    path.write_bytes(glb.content)
    bounds = trimesh.load(path, force="scene").bounds
    assert abs(bounds[0][1]) < 1e-6 and abs(bounds[1][1] - 1.0) < 1e-6  # base at 0, 1 m tall

    # only 3D (the default): even an item planned as 2D gets a model
    ivy = _wait(client, body["assets"]["ivy"]["id"])
    assert ivy["status"] == "done" and ivy["model_url"] is not None
    assert tree["placement"]["surface"] == "floor"


def test_cache_reuses_assets(tmp_path):
    client = _client(tmp_path)
    a = client.post("/assets", json={"prompt": "a red bench", "want_3d": False}).json()
    _wait(client, a["id"])
    b = client.post("/assets", json={"prompt": "A red bench ", "want_3d": False}).json()
    assert a["id"] == b["id"] and b["status"] == "billboard_ready" and not b["pending"]


def test_flood_effect(tmp_path):
    client = _client(tmp_path)
    body = client.post("/plan", json={"prompt": "What if this place were flooded?", "generate": False}).json()
    assert body["plan"]["effects"][0]["type"] == "level"
    assert body["assets"] == {}


def test_models_are_unlit_and_not_metallic(tmp_path):
    """glTF's default material is fully metallic (renders almost black in Unity)."""
    import json
    import struct

    client = _client(tmp_path)
    a = client.post("/assets", json={"prompt": "a young oak tree", "want_3d": True}).json()
    info = _wait(client, a["id"])
    glb = client.get(info["model_url"]).content
    gltf = json.loads(glb[20:20 + struct.unpack_from("<I", glb, 12)[0]])
    for prim in gltf["meshes"][0]["primitives"]:
        mat = gltf["materials"][prim["material"]]
        assert mat["pbrMetallicRoughness"]["metallicFactor"] == 0.0
        assert "KHR_materials_unlit" in mat["extensions"]
    assert "KHR_materials_unlit" in gltf["extensionsUsed"]


def test_orient_outward_flips_inside_out_meshes():
    import numpy as np
    import trimesh

    from whatif.generators import orient_outward

    def outward_share(m):
        c = m.vertices.mean(axis=0)
        return np.mean(((m.triangles_center - c) * m.face_normals).sum(axis=1) > 0)

    sphere = trimesh.creation.icosphere()
    open_sphere = trimesh.Trimesh(sphere.vertices, sphere.faces[: len(sphere.faces) * 3 // 4])  # not watertight
    open_sphere.faces = open_sphere.faces[:, ::-1]  # inside out
    assert outward_share(open_sphere) < 0.5
    orient_outward(open_sphere)
    assert outward_share(open_sphere) > 0.5
    orient_outward(open_sphere)  # already correct: stays
    assert outward_share(open_sphere) > 0.5


def test_transcribe_rejects_non_wav(tmp_path):
    client = _client(tmp_path)
    r = client.post("/transcribe", content=b"not a wav file" * 10, headers={"Content-Type": "audio/wav"})
    assert r.status_code == 400


def test_read_wav_resamples_to_16k():
    import io
    import wave

    import numpy as np

    from whatif.speech import read_wav

    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:  # 1 s stereo 48 kHz
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(48000)
        w.writeframes((np.zeros((48000, 2)) + 1000).astype("<i2").tobytes())
    audio = read_wav(buf.getvalue())
    assert len(audio) == 16000 and abs(audio.mean() - 1000 / 32768) < 1e-4


def test_plan_only_uses_detected_surfaces(tmp_path):
    """Objects for surfaces the headset hasn't detected: table objects go to the floor; wall objects are
    kept with a hint (the headset places them once it knows a wall, e.g. from its own wall estimate)."""
    client = _client(tmp_path)
    floor_only = {"planes": [{"id": "f", "label": "floor", "width_m": 6, "height_m": 5,
                              "distance_m": 1.5, "height_rel_head_m": -1.6}], "head_height_m": 1.6}
    plan = client.post("/plan", json={"prompt": "What if this place had more plants?", "scene": floor_only,
                                      "generate": False}).json()["plan"]
    surfaces = {i["placement"]["surface"] for i in plan["items"]}
    assert "table" not in surfaces, surfaces
    assert "ivy" in {i["id"] for i in plan["items"]}
    assert "wall" in plan["note_for_user"]


def _room(*extra):
    return {"planes": [{"id": "floor-123", "label": "floor", "width_m": 6, "height_m": 5, "distance_m": 1.5,
                        "height_rel_head_m": -1.6}, *extra], "head_height_m": 1.6}


def test_chairs_target_a_detected_table(tmp_path):
    client = _client(tmp_path)
    table = {"id": "plane-abc", "label": "table", "width_m": 1.6, "height_m": 0.9, "distance_m": 2.0,
             "height_rel_head_m": -0.85, "bearing_deg": -15, "height_above_floor_m": 0.75}
    plan = client.post("/plan", json={"prompt": "What if there were chairs for a dinner?", "scene": _room(table),
                                      "generate": False}).json()["plan"]
    chairs = next(i for i in plan["items"] if i["id"] == "chair")
    assert chairs["placement"]["layout"] == "around"
    assert chairs["placement"]["target_id"] == "plane-abc"  # S2 mapped back to the headset's id
    assert chairs["placement"]["surface"] == "floor"


def test_chairs_target_a_generated_table_listed_first(tmp_path):
    client = _client(tmp_path)
    plan = client.post("/plan", json={"prompt": "What if this place had a dining table with chairs?",
                                      "scene": _room(), "generate": False}).json()["plan"]
    ids = [i["id"] for i in plan["items"]]
    assert ids.index("dining_table") < ids.index("chair")
    assert next(i for i in plan["items"] if i["id"] == "chair")["placement"]["target_id"] == "dining_table"


def test_surface_numbers_are_drawn_into_the_image():
    import base64
    import io

    from PIL import Image

    from whatif.planner import mark_surfaces
    from whatif.schemas import PlanRequest

    buf = io.BytesIO()
    Image.new("RGB", (640, 480), (90, 90, 90)).save(buf, format="JPEG")
    req = PlanRequest(prompt="x", image_jpeg_b64=base64.b64encode(buf.getvalue()).decode(), scene=_room(
        {"id": "t", "label": "table", "width_m": 1, "height_m": 1, "distance_m": 2, "height_rel_head_m": -1,
         "view_x": 0.5, "view_y": 0.6}))
    marked = Image.open(io.BytesIO(base64.b64decode(mark_surfaces(req))))
    r, g, b = marked.getpixel((int(0.5 * 640) - 20, int(0.6 * 480)))  # inside the yellow mark, left of the text
    assert r > 200 and g > 180 and b < 80


def test_cutout_survives_empty_background_removal(monkeypatch):
    """An object filling the frame made background removal return nothing; generation then failed
    and the object never got a cut-out."""
    from PIL import Image

    from whatif import generators as g

    class Provider:
        def generate(self, prompt, seed=0):
            img = Image.new("RGB", (512, 512), "white")
            img.paste((120, 80, 40), (100, 100, 400, 500))
            return img

    monkeypatch.setattr(g, "_remove_background", lambda img: Image.new("RGBA", img.size, (0, 0, 0, 0)))
    cut, tries = g._generate_cutout(Provider(), "a grocery shelf")
    assert cut.size[0] > 200 and cut.size[1] > 300  # the white-key fallback kept the object


def test_thin_rule_only_for_triposr():
    from whatif import planner

    original = planner.settings.mesh_provider
    try:
        object.__setattr__(planner.settings, "mesh_provider", "triposr")
        assert "bicycles" in planner.system_prompt()
        object.__setattr__(planner.settings, "mesh_provider", "spar3d")
        assert "bicycles" not in planner.system_prompt() and "{thin_rule}" not in planner.system_prompt()
    finally:
        object.__setattr__(planner.settings, "mesh_provider", original)


def _objects():
    def o(i, group, label, bearing):
        return {"id": i, "group": group, "label": label, "asset_prompt": f"a {label}", "bearing_deg": bearing,
                "distance_m": 2.0, "height_m": 0.9, "footprint_m": 0.5}
    return [o("chair#1", "chair", "chair", -20), o("chair#2", "chair", "chair", 20),
            o("penguin_chick#1", "penguin_chick", "penguin chick", 0), o("tree#1", "tree", "tree", 40)]


def test_edit_expands_groups(tmp_path):
    client = _client(tmp_path)
    r = client.post("/edit", json={"utterance": "make the chairs bigger", "objects": _objects()}).json()
    op = r["operations"][0]
    assert op["type"] == "scale" and op["scale_factor"] == 1.5
    assert sorted(op["targets"]) == ["chair#1", "chair#2"]  # the group name expanded to its instances


def test_edit_it_means_what_the_user_looked_at(tmp_path):
    client = _client(tmp_path)
    r = client.post("/edit", json={"utterance": "delete it", "objects": _objects(), "gaze_id": "tree#1"}).json()
    assert r["operations"][0]["type"] == "delete" and r["operations"][0]["targets"] == ["tree#1"]


def test_edit_restyle_and_add_start_generation(tmp_path):
    client = _client(tmp_path)
    r = client.post("/edit", json={"utterance": "make the tree blue", "objects": _objects()}).json()
    op = r["operations"][0]
    assert op["type"] == "restyle" and op["targets"] == ["tree#1"] and op["asset"]["id"]
    _wait(client, op["asset"]["id"])
    r = client.post("/edit", json={"utterance": "add a chair", "objects": _objects()}).json()
    op = r["operations"][0]
    assert op["type"] == "add" and op["item"]["id"] != "chair"  # unique next to the existing group
    _wait(client, op["asset"]["id"])


def test_edit_ignores_unknown_targets_and_chatter(tmp_path):
    client = _client(tmp_path)
    r = client.post("/edit", json={"utterance": "I'll get a coffee", "objects": _objects()}).json()
    assert r["operations"] == [] and r["reply"] == ""
    r = client.post("/edit", json={"utterance": "undo", "objects": _objects()}).json()
    assert r["operations"][0]["type"] == "undo"


def test_phantom_transcripts_are_not_speech():
    from types import SimpleNamespace

    from whatif.speech import is_speech

    seg = [SimpleNamespace(no_speech_prob=0.1, avg_logprob=-0.2)]
    assert is_speech("Make the chairs bigger.", seg)
    assert not is_speech("Thank you.", seg)
    assert not is_speech("Untertitel im Auftrag des ZDF, 2017", seg)
    assert not is_speech("Some words", [SimpleNamespace(no_speech_prob=0.9, avg_logprob=-1.2)])
    assert not is_speech("is a a a a a a a a", seg)


_ROOM = {
    "planes": [
        {"id": "floor-1", "label": "floor", "width_m": 5, "height_m": 4, "distance_m": 1, "height_rel_head_m": -1.6},
        {"id": "wall-1", "label": "wall", "width_m": 4, "height_m": 2.6, "distance_m": 2, "height_rel_head_m": -0.3},
        {"id": "wall-2", "label": "wall", "width_m": 3, "height_m": 2.6, "distance_m": 2.5, "height_rel_head_m": -0.3},
    ],
    "head_height_m": 1.6,
}


def test_plan_paints_detected_walls(tmp_path):
    client = _client(tmp_path)
    plan = client.post("/plan", json={"prompt": "What if this place had blue painted walls?", "scene": _ROOM}).json()["plan"]
    paint = [e for e in plan["effects"] if e["type"] == "surface"]
    assert paint and paint[0]["surfaces"] == ["wall"] and paint[0]["tint_hex"] == "#3050c0"


def test_paint_without_detected_walls_asks_to_look_around(tmp_path):
    client = _client(tmp_path)
    floor_only = {"planes": _ROOM["planes"][:1], "head_height_m": 1.6}
    plan = client.post("/plan", json={"prompt": "What if this place had blue painted walls?", "scene": floor_only}).json()["plan"]
    assert not [e for e in plan["effects"] if e["type"] == "surface"]
    assert "Look at the surfaces" in plan["note_for_user"]


def test_paint_surfaces_resolve_numbers_and_kinds():
    from whatif.planner import clean_hex, resolve_surfaces
    from whatif.schemas import PlanRequest

    req = PlanRequest(prompt="x", scene=_ROOM)
    assert resolve_surfaces(["S2", "walls", "sky", "S9"], req) == ["wall-1", "wall"]
    assert clean_hex("#35C") == "#3355cc" and clean_hex("blue") is None


def test_edit_paint_op(tmp_path):
    client = _client(tmp_path)
    r = client.post("/edit", json={"utterance": "paint the walls green", "objects": _objects(), "scene": _ROOM}).json()
    op = r["operations"][0]
    assert op["type"] == "paint" and op["targets"] == ["wall"] and op["color_hex"] == "#3a8a40"


def test_floor_item_ignores_a_ceiling_target():
    # seen on the headset: a giraffe "on the floor" with the ceiling as target hung 2.5 m below it
    from whatif.planner import _resolve_targets
    from whatif.schemas import PlanRequest, ScenePlan

    ceiling = {"id": "plane-ceil", "label": "ceiling", "width_m": 5, "height_m": 4, "distance_m": 1.0,
               "height_rel_head_m": 1.9}
    req = PlanRequest(prompt="x", scene=_room(ceiling))
    plan = ScenePlan.model_validate({
        "interpretation": "x", "realism": "playful", "note_for_user": "", "effects": [],
        "items": [{"id": "giraffe", "label": "giraffe", "asset_prompt": "a giraffe", "want_3d": True,
                   "placement": {"surface": "floor", "layout": "single", "facing": "toward_user",
                                 "target_id": "S2", "count": 1, "height_m": 5.5, "footprint_m": 2,
                                 "height_jitter": 0, "min_spacing_m": 1, "max_distance_m": 6}}]})
    out = _resolve_targets(plan, req)
    assert out.items[0].placement.target_id is None


def _jpeg_b64(w=640, h=360):
    import base64
    import io

    from PIL import Image

    buf = io.BytesIO()
    Image.new("RGB", (w, h), (90, 90, 90)).save(buf, format="JPEG")
    return base64.b64encode(buf.getvalue()).decode()


def test_image_points_are_parsed_and_clamped():
    from whatif.planner import parse_image_point

    assert parse_image_point("IMG 0.62,0.71") == "img:0.620,0.710"
    assert parse_image_point("img(0.5, 1.4)") == "img:0.500,1.000"
    assert parse_image_point("S2") is None
    assert parse_image_point("") is None


def test_plan_keeps_an_image_point_target():
    # "a mouse on my keyboard": the keyboard is no surface, only a point in the camera image
    from whatif.planner import _resolve_targets
    from whatif.schemas import PlanRequest, ScenePlan

    req = PlanRequest(prompt="x", image_jpeg_b64=_jpeg_b64(), scene=_room())
    plan = ScenePlan.model_validate({
        "interpretation": "x", "realism": "playful", "note_for_user": "", "effects": [],
        "items": [{"id": "mouse", "label": "mouse", "asset_prompt": "a mouse", "want_3d": True,
                   "placement": {"surface": "table", "layout": "single", "facing": "toward_user",
                                 "target_id": "IMG 0.62,0.71", "count": 1, "height_m": 0.05,
                                 "footprint_m": 0.1, "height_jitter": 0, "min_spacing_m": 0.1,
                                 "max_distance_m": 3}}]})
    assert _resolve_targets(plan, req).items[0].placement.target_id == "img:0.620,0.710"
    # without an image there is nothing to point at
    req.image_jpeg_b64 = None
    plan.items[0].placement.target_id = "IMG 0.62,0.71"
    assert _resolve_targets(plan, req).items[0].placement.target_id is None


def test_marked_image_has_the_same_size():
    import base64
    import io

    from PIL import Image

    from whatif.planner import mark_image

    out = mark_image(_jpeg_b64(), [])
    assert Image.open(io.BytesIO(base64.b64decode(out))).size == (640, 360)


def test_no_wall_paint_without_a_colour_in_the_prompt():
    # seen: "medieval" turned into grey walls - a flat plate, not stone
    from whatif.planner import _resolve_paint
    from whatif.schemas import PlanRequest, ScenePlan

    wall = {"id": "w1", "label": "wall", "width_m": 4, "height_m": 2.5, "distance_m": 2, "height_rel_head_m": -0.3}
    effect = {"type": "surface", "tint_hex": "#8a8580", "strength": 0.8, "surfaces": ["wall"]}
    make = lambda: ScenePlan.model_validate({"interpretation": "x", "realism": "playful", "note_for_user": "",
                                             "items": [], "effects": [effect]})
    medieval = PlanRequest(prompt="What if this was a medieval castle?", scene=_room(wall))
    assert _resolve_paint(make(), medieval).effects == []
    blue = PlanRequest(prompt="Was wäre, wenn die Wände blau gestrichen wären?", scene=_room(wall))
    assert len(_resolve_paint(make(), blue).effects) == 1


def test_user_speech_is_not_logged(tmp_path, caplog, monkeypatch):
    # privacy: with WHATIF_LOG_CONTENT=0, prompts appear in the log only as a word count
    import logging

    import dataclasses

    client = _client(tmp_path)  # (re)loads the app modules: patch after it
    import whatif.main as main_now

    monkeypatch.setattr(main_now, "settings", dataclasses.replace(main_now.settings, log_user_content=False))
    with caplog.at_level(logging.INFO, logger="whatif.api"):
        client.post("/plan", json={"prompt": "What if Tim had a secret garden?", "scene": _room(), "generate": False})
    text = "\n".join(r.getMessage() for r in caplog.records)
    assert "secret garden" not in text and "words>" in text


def test_health_reports_what_is_still_loading(tmp_path):
    body = _client(tmp_path).get("/health").json()
    assert body["ok"] is True and body["models"] == "ready"  # mock image provider: nothing to load
    assert body["ready"] == (body["loading"] == [])


def test_local_planner_request_and_answer(monkeypatch):
    # the local planner gets the image as a data URL, the schema in the prompt and as guided-decoding
    # constraint, and its JSON answer is parsed into the same plan type as Claude's
    import json as _json

    import httpx

    from whatif import planner
    from whatif.schemas import PlanRequest

    seen = {}
    answer = {"interpretation": "A tree", "realism": "realistic", "note_for_user": "", "effects": [],
              "items": [{"id": "tree", "label": "tree", "asset_prompt": "a young oak tree", "want_3d": True,
                         "placement": {"surface": "floor", "layout": "single", "facing": "random", "target_id": None,
                                       "count": 1, "height_m": 3, "footprint_m": 2, "height_jitter": 0.1,
                                       "min_spacing_m": 1, "max_distance_m": 6}}]}

    def fake_post(url, json, timeout):
        seen["url"], seen["body"] = url, json
        return httpx.Response(200, json={"choices": [{"message": {"content": _json.dumps(answer)}, "finish_reason": "stop"}],
                                         "usage": {"prompt_tokens": 10, "completion_tokens": 5}})

    monkeypatch.setattr(httpx, "post", fake_post)
    req = PlanRequest(prompt="What if there was a tree?", image_jpeg_b64=_jpeg_b64(), scene=_room())
    plan = planner.plan_scene(req, provider="local")
    assert plan.items[0].id == "tree"
    assert seen["url"].endswith("/chat/completions")
    user = seen["body"]["messages"][1]["content"]
    assert user[0]["type"] == "image_url" and user[0]["image_url"]["url"].startswith("data:image/jpeg;base64,")
    assert seen["body"]["response_format"]["type"] == "json_schema"
    assert "JSON schema" in seen["body"]["messages"][0]["content"]


def test_local_planner_unreachable_is_a_planner_error(monkeypatch):
    import httpx
    import pytest

    from whatif import planner
    from whatif.schemas import PlanRequest

    def fail(*a, **k):
        raise httpx.ConnectError("refused")

    monkeypatch.setattr(httpx, "post", fail)
    with pytest.raises(planner.PlannerError, match="vLLM"):
        planner.plan_scene(PlanRequest(prompt="What if?", scene=_room()), provider="local")


def test_natural_sentences_are_never_sent_online(monkeypatch):
    # without "What if" a sentence may be anyone's conversation: with Claude as planner it is not sent
    from whatif import planner
    from whatif.schemas import PlanRequest

    def must_not_run(*a, **k):
        raise AssertionError("sent to Claude")

    monkeypatch.setattr(planner, "claude_parse", must_not_run)
    plan = planner.plan_scene(PlanRequest(prompt="a jungle here would be cool", natural=True, scene=_room()),
                              provider="claude")
    assert plan.request is False and plan.items == []


def test_local_planner_decides_a_natural_sentence_is_chatter(monkeypatch):
    import json as _json

    import httpx

    from whatif import planner
    from whatif.schemas import PlanRequest

    chatter = {"request": False, "interpretation": "", "realism": "realistic", "note_for_user": "", "effects": [],
               "items": []}
    monkeypatch.setattr(httpx, "post", lambda url, json, timeout: httpx.Response(
        200, json={"choices": [{"message": {"content": _json.dumps(chatter)}, "finish_reason": "stop"}]}))
    plan = planner.plan_scene(PlanRequest(prompt="where is my phone?", natural=True, scene=_room()), provider="local")
    assert plan.request is False and plan.items == [] and plan.effects == []


def test_edit_can_start_a_new_scene(tmp_path, monkeypatch):
    from whatif import editor
    from whatif.schemas import EditPlan

    monkeypatch.setattr(editor, "mock_edit", lambda req: EditPlan(reply="", operations=[],
                                                                  new_scene="What if this was a jungle?"))
    client = _client(tmp_path)
    body = client.post("/edit", json={"utterance": "now make it a jungle", "scene": _room(), "objects": [],
                                      "generate": False}).json()
    assert body["new_scene"] == "What if this was a jungle?" and body["operations"] == []


def test_later_env_lines_win(tmp_path, monkeypatch):
    # `echo WHATIF_X=new >> .env` must override an earlier WHATIF_X=old
    import os

    from whatif.config import _load_dotenv

    monkeypatch.delenv("WHATIF_TEST_SETTING", raising=False)
    env = tmp_path / ".env"
    env.write_text("WHATIF_TEST_SETTING=old\n# comment\nWHATIF_TEST_SETTING=new\n", encoding="utf-8")
    _load_dotenv(env)
    assert os.environ["WHATIF_TEST_SETTING"] == "new"
    monkeypatch.delenv("WHATIF_TEST_SETTING")


def _plan_with(items, effects=(), note="", interpretation="x"):
    from whatif.schemas import ScenePlan

    def item(id_, label, prompt, surface="floor", layout="single", target=None, count=1):
        return {"id": id_, "label": label, "asset_prompt": prompt, "want_3d": True,
                "placement": {"surface": surface, "layout": layout, "facing": "toward_user", "target_id": target,
                              "count": count, "height_m": 1, "footprint_m": 0.5, "height_jitter": 0,
                              "min_spacing_m": 0.5, "max_distance_m": 6}}
    return ScenePlan.model_validate({"interpretation": interpretation, "realism": "playful", "note_for_user": note,
                                     "effects": list(effects), "items": [item(*i) for i in items]})


def test_flat_items_become_solid_or_are_dropped():
    from whatif.planner import _sanitize

    plan = _sanitize(_plan_with([("poster", "colorful poster", "a vintage movie poster", "wall", "scatter"),
                                 ("rug", "persian rug", "a red persian rug"),
                                 ("tree", "oak tree", "a young oak tree")]))
    ids = [i.id for i in plan.items]
    assert ids == ["poster", "tree"]
    assert "shadow box" in plan.items[0].asset_prompt


def test_beside_the_floor_becomes_a_normal_placement():
    from whatif.planner import _resolve_targets
    from whatif.schemas import PlanRequest

    req = PlanRequest(prompt="x", scene=_room())
    plan = _resolve_targets(_plan_with([("giraffe", "giraffe", "a giraffe", "floor", "beside", "S1")]), req)
    p = plan.items[0].placement
    assert p.layout.value == "single" and p.target_id is None


def test_notes_claiming_missing_effects_are_dropped():
    from whatif.planner import _check_texts

    plan = _check_texts(_plan_with([("pillar", "stone pillar", "a stone pillar")],
                                   effects=[{"type": "tint", "tint_hex": "#3d3226", "strength": 0.3, "surfaces": []}],
                                   note="Real walls are painted with a dark tint."))
    assert plan.note_for_user == ""


def test_there_becomes_the_gaze_target_only_with_a_looked_at_point():
    from whatif.planner import _resolve_targets
    from whatif.schemas import PlanRequest

    make = lambda: _plan_with([("lamp", "floor lamp", "a floor lamp", "floor", "single", "GAZE")])
    seen = PlanRequest(prompt="a lamp there", scene=_room(), looked_at="the floor, 2.3 m ahead, slightly left")
    assert _resolve_targets(make(), seen).items[0].placement.target_id == "gaze"
    unseen = PlanRequest(prompt="a lamp there", scene=_room())
    assert _resolve_targets(make(), unseen).items[0].placement.target_id is None


def test_fillers_get_their_own_lighter_asset():
    from whatif.assets import asset_id

    assert asset_id("a fern") != asset_id("a fern", lite=True)
    assert asset_id("a fern", lite=True) == asset_id("A fern ", lite=True)


def test_environment_plans_keep_more_kinds_and_put_heroes_first():
    from whatif.planner import _sanitize

    items = [(f"k{i}", f"kind {i}", f"a thing {i}") for i in range(14)]
    plan = _plan_with(items)
    plan.scope = "environment"
    for it in plan.items[:10]:
        it.role = "filler"
    plan = _sanitize(plan)
    assert len(plan.items) == 14
    assert plan.items[0].role == "hero"
    single = _sanitize(_plan_with(items))
    assert len(single.items) == 6


def test_sound_loops_are_crossfaded_and_ids_stable():
    import numpy as np

    from whatif.sounds import _loopable, sound_id

    x = np.ones(1000, dtype=np.float32)
    assert len(_loopable(x, 100)) == 900
    assert sound_id("a clock ticking", True) == sound_id("A clock ticking ", True) != sound_id("a clock ticking", False)


def test_behaviours_are_cleaned_and_movers_get_a_side_view():
    from whatif.planner import _sanitize

    plan = _plan_with([("dog", "dog", "a friendly brown dog, full body")])
    plan.items[0].behaviour = {"states": [
        {"name": "idle", "idle": ["breathe", "look_at_user"], "next": [{"event": "user_looks", "goto": "come"},
                                                                        {"event": "timer", "value": 5, "goto": "nowhere"}]},
        {"name": "come", "move": "to_user", "gait": "trot", "speed": 99, "next": [{"event": "arrived", "goto": "idle"}]}]}
    from whatif.schemas import Behaviour
    plan.items[0].behaviour = Behaviour.model_validate(plan.items[0].behaviour)
    item = _sanitize(plan).items[0]
    assert [t.goto for t in item.behaviour.states[0].next] == ["come"]  # unknown state dropped
    assert item.behaviour.states[1].speed == 4.0
    assert item.behaviour.states[1].move == "none"  # walking without a walking animation: stays in place


def test_flyers_swimmers_and_vehicles_keep_moving_with_a_side_view():
    from whatif.planner import _sanitize
    from whatif.schemas import Behaviour

    plan = _plan_with([("fish", "fish", "a clownfish, full body")])
    plan.items[0].behaviour = Behaviour.model_validate({"states": [
        {"name": "swim", "move": "wander", "gait": "swim", "speed": 0.3}]})
    item = _sanitize(plan).items[0]
    assert item.behaviour.states[0].move == "wander"
    assert item.asset_prompt.endswith("side view, facing right")


def test_moving_parts_are_split_into_nodes(tmp_path):
    import trimesh

    from whatif.parts import PartBox, parse_requests, split

    assert parse_requests(["tail:wag", "Head nod", "legs:dance"]) == [("tail", "wag"), ("head", "nod")]
    body = trimesh.creation.box(extents=(0.8, 0.6, 0.4)).subdivide().subdivide()
    body.apply_translation((0, 0.3, 0))
    tail = trimesh.creation.box(extents=(0.3, 0.1, 0.1)).subdivide().subdivide()
    tail.apply_translation((0.55, 0.55, 0))
    glb = tmp_path / "dog.glb"
    glb.write_bytes(trimesh.Scene(trimesh.util.concatenate([body, tail])).export(file_type="glb"))
    # the tail sits at the right end, upper part of the image
    made = split(glb, [PartBox(name="tail", motion="wag", box=[0.78, 0.0, 1.0, 0.3], pivot=[0.78, 0.15])])
    assert made == 1
    nodes = trimesh.load(glb, force="scene").graph.nodes_geometry
    assert any(n.startswith("part__tail__wag") for n in nodes) and "body" in nodes


# --------------------------------------------------------------------------- web pictures

class _Resp:
    def __init__(self, data=None, content=b""):
        self._data, self.content = data, content

    def json(self):
        return self._data


def _fake_web(monkeypatch, died, image="Portrait.jpg", licence="Public domain"):
    """Wikidata says the query is a human who died in `died` (None: alive); Commons answers with one file."""
    import dataclasses
    import io as _io

    from PIL import Image as _Image

    from whatif import web_images

    monkeypatch.setattr(web_images, "settings", dataclasses.replace(web_images.settings, image_provider="sdxl_turbo",
                                                                    web_images=True, web_candidates=2))
    buf = _io.BytesIO()
    _Image.new("RGB", (400, 600), "gray").save(buf, format="JPEG")
    calls = []

    def get(url, params=None):
        calls.append((url, params))
        if "wikidata" in url and params["action"] == "wbsearchentities":
            return _Resp({"search": [{"id": "Q1"}]})
        if "wikidata" in url:
            claims = {"P31": [{"mainsnak": {"datavalue": {"value": {"id": "Q5"}}}}],
                      "P18": [{"mainsnak": {"datavalue": {"value": image}}}]}
            if died:
                claims["P570"] = [{"mainsnak": {"datavalue": {"value": {"time": f"+{died}-03-22T00:00:00Z"}}}}]
            return _Resp({"entities": {"Q1": {"claims": claims}}})
        if "commons" in url:
            return _Resp({"query": {"pages": {"1": {"imageinfo": [{
                "mime": "image/jpeg", "thumburl": "https://upload.example/p.jpg",
                "extmetadata": {"LicenseShortName": {"value": licence}}}]}}}})
        return _Resp(content=buf.getvalue())

    monkeypatch.setattr(web_images, "_get", get)
    return web_images, calls


def test_web_pictures_of_historical_figures_only(monkeypatch):
    web, calls = _fake_web(monkeypatch, died=1832)
    assert len(web.find("Johann Wolfgang von Goethe", "person")) == 1
    web, calls = _fake_web(monkeypatch, died=None)  # alive: never searched for pictures
    assert web.find("Some Living Person", "person") == []
    assert not any("commons" in u or "openverse" in u for u, _ in calls)
    web, calls = _fake_web(monkeypatch, died=1990)  # died too recently
    assert web.find("Someone", "person") == []
    web, calls = _fake_web(monkeypatch, died=None)  # a "thing" query that names a living human
    assert web.find("Some Living Person", "thing") == []


def test_web_pictures_need_a_free_licence(monkeypatch):
    web, _ = _fake_web(monkeypatch, died=1832, licence="CC BY-SA 4.0")
    assert web.find("Johann Wolfgang von Goethe", "person") == []


def test_web_pictures_compete_with_generated_ones():
    from PIL import Image as _Image, ImageDraw

    from whatif import generators

    real = _Image.new("RGB", (512, 512), "white")
    ImageDraw.Draw(real).ellipse((150, 120, 360, 400), fill="red")
    cutout, _ = generators._generate_cutout(generators.MockImageProvider(), "a red ball", [real])
    assert cutout.width > 0


def test_hand_items_stay_small_and_still():
    from whatif.planner import _sanitize
    from whatif.schemas import Behaviour, BehaviourState, Surface

    from whatif.planner import mock_plan
    from whatif.schemas import PlanRequest

    plan = mock_plan(PlanRequest(prompt="What if there was a dog?"))
    item = plan.items[0]
    item.placement.surface, item.placement.count, item.placement.height_m = Surface.hand, 3, 0.8
    item.behaviour = Behaviour(states=[BehaviourState(name="fly", move="flee_user")])
    item = _sanitize(plan).items[0]
    assert (item.placement.count, item.placement.height_m) == (1, 0.3)
    assert item.behaviour.states[0].move == "none"


def test_tint_only_for_light_or_mood(monkeypatch):
    import dataclasses

    from whatif import planner

    monkeypatch.setattr(planner, "settings", dataclasses.replace(planner.settings, tint=True))
    from whatif.planner import _resolve_paint
    from whatif.schemas import Effect, EffectType, PlanRequest

    for prompt, kept in [("What if this room were a jungle?", 0), ("What if it were night?", 1)]:
        from whatif.planner import mock_plan

        plan = mock_plan(PlanRequest(prompt=prompt))
        plan.effects = [Effect(type=EffectType.tint, colors=["#1e3d1e"], strength=0.9)]
        out = _resolve_paint(plan, PlanRequest(prompt=prompt))
        assert len(out.effects) == kept
        if kept:
            assert out.effects[0].tint_hex and out.effects[0].strength <= 0.6


def test_trellis_provider_uses_the_worker(tmp_path, monkeypatch):
    """The TRELLIS provider sends the cut-out to the worker and normalises what comes back."""
    import httpx
    import trimesh
    from PIL import Image as _Image

    from whatif import generators

    box = trimesh.creation.box(extents=(0.4, 1.0, 0.3))
    glb = trimesh.Scene(box).export(file_type="glb")
    sent = {}

    def post(url, content=None, params=None, timeout=None):
        sent.update(url=url, params=params, size=len(content))
        return httpx.Response(200, content=glb)

    monkeypatch.setattr(httpx, "post", post)
    cut = tmp_path / "cut.png"
    _Image.new("RGBA", (64, 64), (255, 0, 0, 255)).save(cut)
    out = tmp_path / "model.glb"
    generators.TrellisMeshProvider().generate(cut, out, max_faces=5000)
    assert out.exists() and sent["url"].endswith("/mesh") and sent["params"]["faces"] == 5000
    scene = trimesh.load(out, force="scene")
    assert abs(scene.extents[1] - 1.0) < 0.05  # normalised: 1 m tall


def _fake_objaverse(tmp_path, monkeypatch, picture_score):
    """A two-object index, a fake CLIP and fake downloads."""
    import dataclasses
    import io as _io
    import json as _json

    import numpy as np
    import trimesh
    from PIL import Image as _Image

    from whatif import generators, objaverse

    root = tmp_path / "objaverse"
    root.mkdir()
    emb = np.array([[1, 0] + [0] * 510, [0, 1] + [0] * 510], dtype=np.float16)
    np.save(root / "embeddings.npy", emb)
    entries = [
        {"uid": "aaa", "name": "Grand piano", "author": "Ann", "license": "by", "path": "glbs/a.glb",
         "faces": 5000, "mb": 2.0, "thumb": "https://media.example/a.jpg", "text": "a black grand piano"},
        {"uid": "bbb", "name": "Piano (NC)", "author": "Bob", "license": "by-nc", "path": "glbs/b.glb",
         "faces": 5000, "mb": 2.0, "thumb": "https://media.example/b.jpg", "text": "a piano"},
    ]
    (root / "entries.jsonl").write_text("\n".join(_json.dumps(e) for e in entries), encoding="utf-8")
    monkeypatch.setattr(objaverse, "settings", dataclasses.replace(objaverse.settings, data_dir=tmp_path))
    monkeypatch.setattr(objaverse, "_index", None)
    monkeypatch.setattr(objaverse, "_index_failed", False)

    class Scorer:
        def text_embedding(self, prompt):
            return [1.0, 0.2] + [0.0] * 510

        def score(self, rgba, prompt):
            return picture_score

    monkeypatch.setattr(generators, "_clip_scorer", lambda: Scorer())
    buf = _io.BytesIO()
    _Image.new("RGB", (64, 64), "gray").save(buf, format="JPEG")
    glb = trimesh.Scene(trimesh.creation.box(extents=(1.5, 1.0, 0.6))).export(file_type="glb")
    monkeypatch.setattr(objaverse, "_get", lambda url, timeout=20.0: glb if url.endswith(".glb") else buf.getvalue())
    return objaverse


def test_objaverse_uses_a_model_that_clearly_fits(tmp_path, monkeypatch):
    objaverse = _fake_objaverse(tmp_path, monkeypatch, picture_score=0.34)
    found = objaverse.find("a black grand piano, realistic photo")
    assert found is not None and found[0]["uid"] == "aaa"  # the NC one is never a candidate
    out = tmp_path / "model.glb"
    credit = objaverse.fetch(found[0], out)
    assert out.exists() and credit["author"] == "Ann" and credit["license"] == "CC BY 4.0"
    assert [c["uid"] for c in objaverse.credits()] == ["aaa"]
    import trimesh

    fitted = trimesh.load(out, force="scene")  # wrapped, not rewritten: 1 m tall, base at 0, centred
    (x0, y0, z0), (x1, y1, z1) = fitted.bounds
    assert abs((y1 - y0) - 1.0) < 1e-4 and abs(y0) < 1e-4 and abs(x0 + x1) < 1e-4 and abs(z0 + z1) < 1e-4


def test_objaverse_skips_weak_matches(tmp_path, monkeypatch):
    objaverse = _fake_objaverse(tmp_path, monkeypatch, picture_score=0.22)
    assert objaverse.find("a black grand piano") is None


def test_image_prompts_describe_one_single_object():
    from whatif.planner import _single

    assert _single("colorful climbing holds, plastic") == "a single colorful climbing hold, plastic"
    assert _single("a school of clownfish") == "a single clownfish"
    assert _single("a bunch of red berries on a branch") == "a single red berry on a branch"
    assert _single("a single glass vase") == "a single glass vase"
    assert _single("green moss") == "a single green moss"


def test_only_hanging_things_on_the_ceiling():
    from whatif.planner import _sanitize, mock_plan
    from whatif.schemas import PlanRequest, Surface

    plan = mock_plan(PlanRequest(prompt="What if there was a dog?"))
    cup = plan.items[0].model_copy(deep=True)
    cup.id, cup.asset_prompt, cup.label = "cup", "a white coffee cup", "coffee cup"
    lamp = plan.items[0].model_copy(deep=True)
    lamp.id, lamp.asset_prompt, lamp.label = "lamp", "a brass hanging lamp", "lamp"
    cup.placement.surface = lamp.placement.surface = Surface.ceiling
    plan.items = [cup, lamp]
    out = {i.id: i.placement.surface for i in _sanitize(plan).items}
    assert out == {"cup": Surface.horizontal, "lamp": Surface.ceiling}



def test_no_tint_by_default():
    from whatif.planner import _resolve_paint, mock_plan
    from whatif.schemas import Effect, EffectType, PlanRequest

    plan = mock_plan(PlanRequest(prompt="What if it were night?"))
    plan.effects = [Effect(type=EffectType.tint, colors=["#102040"], strength=0.5)]
    assert _resolve_paint(plan, PlanRequest(prompt="What if it were night?")).effects == []


def test_a_named_table_beats_the_gaze_point():
    from whatif.planner import named_surface
    from whatif.schemas import PlanRequest

    table = {"id": "plane-t", "label": "table", "width_m": 1.2, "height_m": 0.8, "distance_m": 1.5,
             "height_rel_head_m": -0.9}
    req = PlanRequest(prompt="x", scene=_room(table))
    assert named_surface("Stell eine Kaffeetasse auf den Tisch", req) == "plane-t"
    assert named_surface("put a coffee cup on the table", req) == "plane-t"
    assert named_surface("put a coffee cup there", req) is None  # "there": the gaze point
    assert named_surface("stell sie da auf den Tisch", req) is None
    assert named_surface("a lamp on the floor", req) is None


def test_coats_are_small_still_fillers_and_drapes_hang_from_tables():
    from whatif.assets import asset_id
    from whatif.planner import _enforce_surfaces, _sanitize, mock_plan
    from whatif.schemas import Behaviour, BehaviourState, Layout, PlanRequest, Surface

    plan = mock_plan(PlanRequest(prompt="What if there was a dog?"))
    coat = plan.items[0].model_copy(deep=True)
    coat.id, coat.asset_prompt = "moss", "a single flat clump of green forest moss"
    coat.placement.layout, coat.placement.surface, coat.placement.height_m = Layout.coat, Surface.wall, 0.9
    coat.placement.density, coat.sound = "", "rustling"
    coat.behaviour = Behaviour(states=[BehaviourState(name="go", move="wander", gait="trot")])
    drape = coat.model_copy(deep=True)
    drape.id, drape.asset_prompt = "ivy", "a single long strand of green ivy"
    drape.placement.layout, drape.placement.surface, drape.placement.height_m = Layout.drape, Surface.floor, 3.0
    plan.items = [coat, drape]
    out = {i.id: i for i in _enforce_surfaces(_sanitize(plan), PlanRequest(prompt="x")).items}
    c, d = out["moss"], out["ivy"]
    assert (c.role, c.behaviour, c.sound, c.placement.surface) == ("filler", None, "", Surface.floor)
    assert c.placement.height_m == 0.35 and c.placement.density == "dense"
    # no table detected: the drape still waits for one instead of lying on the floor
    assert d.placement.surface == Surface.table and d.placement.height_m == 1.0
    assert "hanging straight down" in d.asset_prompt and d.asset_prompt.endswith("front view")
    assert c.asset_prompt.endswith("top-down view") and not c.want_3d and not d.want_3d
    assert asset_id("moss", lite=True, tiny=True) != asset_id("moss", lite=True)


def test_objaverse_model_must_have_the_planned_proportions():
    from whatif.objaverse import shape_fits

    assert not shape_fits(2.0, 1.0 / 4.5)  # a wide stump for a slim 4.5 m trunk
    assert shape_fits(0.35, 1.0 / 4.5)  # a trunk a bit thicker than planned
    assert shape_fits(2.4, 2.2 / 0.9)  # a sofa
    assert not shape_fits(0.3, 2.2 / 0.9)  # a pole for a sofa
    assert shape_fits(5.0, None)  # nothing planned: anything goes


def test_coats_list_joins_the_items_of_an_environment():
    from whatif.planner import _sanitize, mock_plan
    from whatif.schemas import Layout, PlanRequest

    plan = mock_plan(PlanRequest(prompt="What if there was a dog?"))
    coat = plan.items[0].model_copy(deep=True)
    coat.id, coat.asset_prompt = "Moss Coat", "a single flat clump of moss"
    plan.items = [plan.items[0].model_copy(deep=True) for _ in range(20)]
    plan.coats = [coat]
    plan.scope = "environment"
    out = _sanitize(plan.model_copy(deep=True))
    assert len(out.items) == 16 and out.items[-1].id == "moss_coat" and out.coats == []
    assert out.items[-1].placement.layout == Layout.coat and out.items[-1].role == "filler"
    plan.scope = "single"
    assert all(i.id != "moss_coat" for i in _sanitize(plan).items)


def test_edit_prompt_lists_big_scenes_by_group():
    from whatif.editor import _scene_text
    from whatif.schemas import EditRequest, SceneObject

    objs = [SceneObject(id=f"moss#{i}", group="moss", label="moss", asset_prompt="a single clump of moss " * 3,
                        bearing_deg=0, distance_m=1 + i / 10, height_m=0.1, footprint_m=0.2) for i in range(150)]
    objs += [SceneObject(id=f"fern#{i}", group="fern", label="fern", asset_prompt="a fern", bearing_deg=30,
                         distance_m=2, height_m=0.8, footprint_m=0.6, looked_at=i == 9) for i in range(40)]
    text = _scene_text(EditRequest(utterance="make the fern red", objects=objs))
    assert len(text) < 3000 and "fern#9" in text and "LOOKED AT" in text and "and 144 more" in text


def test_coat_patches_get_a_soft_outline_and_are_never_reused(tmp_path, monkeypatch):
    import numpy as np
    from PIL import Image

    from whatif.assets import AssetStore, soft_patch

    full = Image.new("RGBA", (200, 200), (40, 160, 40, 255))  # a picture the moss fills completely
    alpha = np.asarray(soft_patch(full, seed=3))[..., 3]
    assert alpha[100, 100] == 255 and alpha[0, 0] == 0 and alpha[0, 100] == 0 and alpha[100, 199] == 0
    store = AssetStore(tmp_path, 1)
    monkeypatch.setattr(store, "_from_library", lambda *a: (_ for _ in ()).throw(AssertionError("reused")))
    store.request("a single clump of moss, flat, seen from directly above, top-down view", False, tiny=True)


def test_wild_plants_are_told_apart_from_potted_ones():
    from whatif.generators import wild_plant

    assert wild_plant("a single large monstera deliciosa plant with split leaves, realistic photo")
    assert wild_plant("wild banana plant from a tropical rainforest, growing from the ground")
    assert not wild_plant("a single potted fern on a windowsill")
    assert not wild_plant("a vase with red roses")
    assert not wild_plant("a single wooden crate")


def test_a_desert_keeps_its_sandy_floor_but_a_jungle_does_not():
    from whatif.planner import _resolve_paint, mock_plan
    from whatif.schemas import Effect, EffectType, PlaneInfo, PlanRequest, SceneContext

    scene = SceneContext(planes=[PlaneInfo(id="f1", label="floor", width_m=5, height_m=5, distance_m=1,
                                           height_rel_head_m=-1.6),
                                 PlaneInfo(id="w1", label="wall", width_m=4, height_m=2.5, distance_m=2,
                                           height_rel_head_m=0)])
    for prompt, kept in [("Was wäre, wenn dieser Raum eine Wüste wäre?", 1), ("What if this room were a jungle?", 0)]:
        req = PlanRequest(prompt=prompt, scene=scene)
        plan = mock_plan(req)
        plan.effects = [Effect(type=EffectType.surface, surfaces=["floor", "wall"], tint_hex="#d8b98a",
                               colors=["#c49a62"], pattern="noise", strength=0.7)]
        out = _resolve_paint(plan, req).effects
        assert len(out) == kept, prompt
        if kept:
            assert out[0].surfaces == ["floor"]


def test_under_the_table_targets_the_table_and_stands_on_the_floor():
    from whatif.planner import _resolve_targets, mock_plan
    from whatif.schemas import Layout, PlaneInfo, PlanRequest, SceneContext, Surface

    scene = SceneContext(planes=[PlaneInfo(id="t1", label="table", width_m=1.4, height_m=0.8, distance_m=1.5,
                                           height_rel_head_m=-0.9)])
    req = PlanRequest(prompt="What if a cat slept under the table?", scene=scene)
    plan = mock_plan(req)
    p = plan.items[0].placement
    p.layout, p.surface, p.target_id = Layout.under, Surface.table, None
    p = _resolve_targets(plan, req).items[0].placement
    assert p.layout == Layout.under and p.target_id == "t1" and p.surface == Surface.floor


def _streamed_plan_text():
    from whatif.planner import mock_plan
    from whatif.schemas import PlanRequest

    plan = mock_plan(PlanRequest(prompt="What if there were chairs for a dinner?"))
    return plan, plan.model_dump_json()


def test_items_are_handed_on_while_the_plan_is_written():
    from whatif.planner import _EarlyItems
    from whatif.schemas import PlanRequest

    plan, text = _streamed_plan_text()
    got = []
    early = _EarlyItems(PlanRequest(prompt="What if there were chairs for a dinner?"), lambda it, _: got.append(it.id))
    for end in range(7, len(text) + 1, 7):
        early.feed(text[:end])
    early.feed(text)
    assert got and len(got) == len(set(got))
    assert set(got) <= {i.id for i in plan.items}


def test_plan_stream_sends_items_before_the_done_event(monkeypatch):
    import json

    from fastapi.testclient import TestClient

    from whatif import planner
    from whatif.main import app

    plan, text = _streamed_plan_text()

    def fake_stream(url, body, on_text, t0):
        for end in range(10, len(text) + 1, 10):
            on_text(text[:end])
        return text

    from whatif import main

    monkeypatch.setattr(main, "plan_scene", lambda req, on_item=None: planner.plan_scene(req, "local", on_item))
    monkeypatch.setattr(planner, "_local_stream", fake_stream)
    with TestClient(app) as client:
        r = client.post("/plan/stream", json={"prompt": "What if there were chairs for a dinner?", "generate": False})
    events = [json.loads(line) for line in r.text.splitlines() if line.strip()]
    kinds = [e["event"] for e in events]
    assert kinds[-1] == "done" and "item" in kinds and kinds.index("item") < kinds.index("done")
    final = {i["id"] for i in events[-1]["response"]["plan"]["items"]}
    assert {e["item"]["id"] for e in events if e["event"] == "item"} <= final


def test_editor_keeps_relations_and_drops_mood_paint(monkeypatch):
    from whatif import editor
    from whatif.planner import mock_plan
    from whatif.schemas import EditOp, EditOpType, EditPlan, EditRequest, Layout, PlaneInfo, PlanRequest, SceneContext

    scene = SceneContext(planes=[PlaneInfo(id="t1", label="table", width_m=1.4, height_m=0.8, distance_m=1.0,
                                           height_rel_head_m=-0.9)])
    cat = mock_plan(PlanRequest(prompt="What if there was a cat?")).items[0]
    cat.placement.layout, cat.placement.target_id = Layout.single, "S1"
    answer = EditPlan(reply="ok", operations=[
        EditOp.make(EditOpType.add, item=cat, target_id="S1", relation="under"),
        EditOp.make(EditOpType.paint, targets=["wall"], color_hex="#0d0d1a")])
    monkeypatch.setattr(editor, "mock_edit", lambda req: answer)  # the tests run with the mock planner
    out = editor.edit_scene(EditRequest(utterance="What if it were night and a cat slept under the table?",
                                        scene=scene, generate=False))
    kinds = [o.type for o in out.operations]
    assert EditOpType.paint not in kinds
    added = [o for o in out.operations if o.type == EditOpType.add][0]
    assert added.item.placement.layout == Layout.under and added.item.placement.target_id == "t1"


def test_the_table_beats_a_nearer_seat_and_dunes_are_no_objects():
    from whatif.planner import _GROUND_PIECE, named_surface
    from whatif.schemas import PlaneInfo, PlanRequest, SceneContext

    scene = SceneContext(planes=[
        PlaneInfo(id="seat1", label="seat", width_m=0.45, height_m=0.45, distance_m=0.8, height_rel_head_m=-1.2),
        PlaneInfo(id="t1", label="table", width_m=1.4, height_m=0.8, distance_m=1.2, height_rel_head_m=-0.9)])
    req = PlanRequest(prompt="x", scene=scene)
    assert named_surface("What if there was a coffee cup on the table?", req) == "t1"
    assert named_surface("Put a cushion on the chair", req) == "seat1"
    assert _GROUND_PIECE.search("sand dune") and not _GROUND_PIECE.search("sand castle")


def test_a_real_model_must_look_like_the_generated_picture(monkeypatch):
    from PIL import Image

    from whatif import objaverse

    class Idx:
        def search(self, *a):
            return [(0.7, {"uid": "a" * 32, "thumb": "x", "faces": 20000, "name": "Space Taxi"})]

    class Scorer:
        def text_embedding(self, t): return [0.0] * 512
        def score(self, img, prompt): return 0.34
        def picture_similarity(self, a, b): return 0.74

    monkeypatch.setattr(objaverse, "index", lambda: Idx())
    monkeypatch.setattr("whatif.generators._clip_scorer", lambda: Scorer())
    monkeypatch.setattr(objaverse, "_get", lambda url, timeout=8: _png())
    picture = Image.new("RGBA", (64, 64), (255, 200, 0, 255))
    assert objaverse.find("a single yellow taxi car", picture=picture) is None   # looks different
    assert objaverse.find("a single yellow taxi car") is not None               # text alone would take it


def _png():
    import io

    from PIL import Image

    b = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(b, "PNG")
    return b.getvalue()


def test_downloaded_movers_are_turned_to_face_plus_x(monkeypatch):
    import io
    import json
    import struct

    import numpy as np
    import trimesh

    from whatif import objaverse

    # a "dog": long body along +Z with a head at +Z
    body = trimesh.creation.box(extents=(0.3, 0.3, 1.0))
    head = trimesh.creation.box(extents=(0.25, 0.25, 0.25))
    head.apply_translation((0, 0.3, 0.6))
    scene = trimesh.Scene([body, head])
    img = objaverse.render_side(list(scene.dump()), 90.0)
    assert img.size == (384, 384) and np.asarray(img).min() < 250   # something was drawn

    # the vision model "sees" the view turned by 90 deg facing right
    views = iter(["front", "right", "back", "left"])
    monkeypatch.setattr("whatif.parts.facing", lambda pic: next(views))
    assert objaverse.front_yaw(scene) == 90.0

    # the wrap node turns +Z (the head) to +X around the centre
    glb = scene.export(file_type="glb")
    out = objaverse.wrap_glb(glb, 1.0, (0.0, 0.0, 0.0), 90.0, (0.0, 0.0))
    n = struct.unpack_from("<I", out, 12)[0]
    m = np.array(json.loads(out[20:20 + n])["nodes"][-1]["matrix"]).reshape(4, 4).T  # column-major
    assert np.allclose(m @ np.array([0, 0, 1, 1]), [1, 0, 0, 1], atol=1e-6)


def test_movers_accept_side_views_and_the_vision_model_has_the_last_word(monkeypatch):
    from PIL import Image

    from whatif import objaverse

    cands = [(0.89, {"uid": "m" * 32, "thumb": "meerkat", "faces": 25000, "name": "Meerkat"}),
             (0.87, {"uid": "d" * 32, "thumb": "dog", "faces": 15000, "name": "Dog"})]

    class Idx:
        def search(self, *a): return cands

    class Scorer:
        def text_embedding(self, t): return [0.0] * 512
        def score(self, img, prompt): return 0.29
        def picture_similarity(self, a, b): return 0.71   # side view vs three-quarter preview

    monkeypatch.setattr(objaverse, "index", lambda: Idx())
    monkeypatch.setattr("whatif.generators._clip_scorer", lambda: Scorer())
    monkeypatch.setattr(objaverse, "_get", lambda url, timeout=8: _png())
    seen = []
    def judge(thumb, subject):
        seen.append(subject)
        return len(seen) == 2   # the first (meerkat) is not it, the second (dog) is
    monkeypatch.setattr(objaverse, "confirmed", judge)
    pic = Image.new("RGBA", (32, 32), (200, 150, 90, 255))
    prompt = "a single friendly dog, full body, realistic photo, side view, facing right"
    assert objaverse.find(prompt, picture=pic) is None            # still objects: 0.71 is too unlike
    seen.clear()
    found = objaverse.find(prompt, picture=pic, mover=True)
    assert found and found[0]["name"] == "Dog"
    assert seen[0] == "a single friendly dog, realistic photo"     # framing words left out


def test_objaverse_quality_filter_and_ranking(tmp_path):
    import json

    import numpy as np

    from whatif.objaverse import Index, quality

    rows = [{"uid": "a", "license": "by", "faces": 20000, "mb": 3, "likes": 0, "staff": False},
            {"uid": "b", "license": "by", "faces": 20000, "mb": 3, "likes": 40, "staff": False},
            {"uid": "c", "license": "by", "faces": 20000, "mb": 3, "likes": 1, "staff": True},
            {"uid": "d", "license": "by", "faces": 20000, "mb": 3}]          # old index: no signals
    emb = np.zeros((4, 512), dtype=np.float16)
    emb[:, 0] = [1.0, 0.9, 0.8, 0.7]
    np.save(tmp_path / "embeddings.npy", emb)
    (tmp_path / "entries.jsonl").write_text("\n".join(json.dumps(r) for r in rows), encoding="utf-8")
    idx = Index(tmp_path)
    q = np.zeros(512, dtype=np.float32); q[0] = 1.0
    uids = [e["uid"] for _, e in idx.search(q, 4, {"by"}, 80000, 15, 0, min_likes=5)]
    assert uids == ["b", "c", "d"]                                   # 0 likes out; staff pick and old rows stay
    assert [e["uid"] for _, e in idx.search(q, 4, {"by"}, 80000, 15)] == ["a", "b", "c", "d"]
    assert quality(rows[2]) == 1.0 and quality(rows[0]) == 0.0 and 0.75 < quality(rows[1]) < 0.85


def test_animals_get_a_known_front_even_before_they_move(monkeypatch):
    from whatif import editor
    from whatif.planner import _sanitize, could_move, mock_plan
    from whatif.schemas import (Behaviour, BehaviourState, EditPlan, EditRequest, PlanRequest, SceneObject)

    plan = mock_plan(PlanRequest(prompt="What if there was a parrot?"))
    it = plan.items[0]
    it.label, it.asset_prompt, it.behaviour = "parrot", "a single colourful parrot, realistic photo", None
    assert _sanitize(plan).items[0].asset_prompt.endswith("side view, facing right")
    it.label, it.asset_prompt = "parrot statue", "a stone parrot statue"
    assert not could_move(it)

    fly = Behaviour(states=[BehaviourState(name="fly_around", move="circle", speed=1.0, gait="fly", idle=[],
                                           next=[])])
    answer = EditPlan(reply="ok", operations=[], behaviours=[editor.BehaviourChange(targets=["bird"], behaviour=fly)])
    monkeypatch.setattr(editor, "mock_edit", lambda req: answer)
    objs = [SceneObject(id=f"bird#{i}", group="bird", label="bird", asset_prompt="a colourful bird, flying",
                        bearing_deg=0, distance_m=2, height_m=0.3, footprint_m=0.3) for i in range(2)]
    out = editor.edit_scene(EditRequest(utterance="let the birds fly around", objects=objs, generate=False))
    restyles = [o for o in out.operations if o.type.value == "restyle"]
    assert len(restyles) == 1 and restyles[0].asset_prompt.endswith("side view, facing right")
    assert sorted(restyles[0].targets) == ["bird#0", "bird#1"]


def test_single_objects_get_no_ambience(monkeypatch):
    from whatif import planner
    from whatif.schemas import PlanRequest

    def fake(req, provider, early=None):
        p = planner.mock_plan(req)
        p.scope, p.ambience = "single", "soft park sounds, distant birds"
        p.items[0].sound, p.items[0].sound_loop = "soft panting", True
        return p
    monkeypatch.setattr(planner, "_model_plan", fake)
    out = planner.plan_scene(PlanRequest(prompt="What if there was a dog?"), provider="local")
    assert out.ambience == "" and out.items[0].sound == ""
