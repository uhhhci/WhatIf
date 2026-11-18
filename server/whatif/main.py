"""HTTP API for the What-if headset client.

Run:  uvicorn whatif.main:app --host 0.0.0.0 --port 8000
The Magic Leap 2 and the PC must be on the same network; set the PC's LAN IP in
the Unity WhatIfSettings asset.
"""

from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import FileResponse, StreamingResponse

from . import speech
from .assets import store
from .config import settings
from .editor import edit_scene
from .planner import PlannerError, plan_scene
from .schemas import AssetInfo, AssetRequest, EditRequest, EditResponse, Layout, PlanRequest, PlanResponse, SoundInfo
from .sounds import sounds

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("whatif.api")


def _said(text: str) -> str:
    """What a user said, for the log: only its length unless WHATIF_LOG_CONTENT=1 (privacy)."""
    return repr(text) if settings.log_user_content else f"<{len(text.split())} words>"


@asynccontextmanager
async def lifespan(_: FastAPI):
    if settings.image_provider != "mock":
        store.warm_up()  # GPU models take ~20 s to load; do it before the headset asks
    if settings.whisper_model:
        import threading

        threading.Thread(target=speech.warm_up, name="whisper-warmup", daemon=True).start()
    yield


app = FastAPI(title="What if? - generation server", version="0.1.0", lifespan=lifespan)


@app.get("/health")
def health() -> dict:
    from . import generators

    # what is still loading, so the headset can say "the server is starting" instead of failing
    loading = []
    if settings.image_provider != "mock" and generators.models_state == "loading":
        loading.append("the image and 3D generators")
    if settings.whisper_model and not speech.ready():
        loading.append("the speech recognition")
    if settings.planner == "local":
        from .planner import local_planner_reachable

        if not local_planner_reachable():
            loading.append("the planner (language model)")
    if "trellis" in (settings.mesh_provider, settings.hero_mesh_provider):
        if not generators.trellis_reachable():
            loading.append("the 3D generator (TRELLIS)")
    objaverse_state = "off"
    if settings.objaverse:
        from . import objaverse

        idx = objaverse.index()
        objaverse_state = f"{len(idx.entries)} models" if idx else "no index (run scripts/build_objaverse_index.py)"
    return {
        "ok": True,
        "objaverse": objaverse_state,
        "ready": not loading,
        "loading": loading,
        "models": "ready" if settings.image_provider == "mock" else generators.models_state,
        "planner": settings.planner,
        "image_provider": settings.image_provider,
        "mesh_provider": settings.mesh_provider,
    }


def _room_light(image_b64: str | None) -> str | None:
    """Average colour of the camera picture (how bright and warm the room's light is)."""
    if not image_b64:
        return None
    try:
        import base64
        import io

        from PIL import Image

        img = Image.open(io.BytesIO(base64.b64decode(image_b64))).convert("RGB")
        img.thumbnail((64, 64))
        r, g, b = (int(sum(c) / len(c)) for c in zip(*img.getdata()))
        return f"#{r:02x}{g:02x}{b:02x}"
    except Exception:  # noqa: BLE001 - just no light matching
        return None


@app.post("/plan", response_model=PlanResponse)
async def plan(req: PlanRequest) -> PlanResponse:
    image_kb = len(req.image_jpeg_b64) * 3 // 4 // 1024 if req.image_jpeg_b64 else 0
    log.info("plan: %s, %d planes, camera image: %s", _said(req.prompt), len(req.scene.planes),
             f"{image_kb} kB" if image_kb else "none")
    try:
        scene_plan = await run_in_threadpool(plan_scene, req)
    except PlannerError as e:
        raise HTTPException(status_code=502, detail=str(e)) from e
    return _respond(req, scene_plan)


def _asset_for(item) -> AssetInfo:
    return store.request(item.asset_prompt, item.want_3d, lite=item.role == "filler",
                         parts=item.parts, web=(item.web_search, item.web_kind),
                         tiny=item.placement.layout in (Layout.coat, Layout.drape),
                         shape=item.placement.footprint_m / max(0.05, item.placement.height_m), name=item.label)


@app.post("/plan/stream")
async def plan_stream(req: PlanRequest) -> StreamingResponse:
    """Like /plan, as newline-separated JSON events while the planner is still writing:
    {"event": "item", "item": ..., "asset": ..., "interpretation": ...} for each object as soon as it is
    planned (its generation starts right away), then {"event": "done", "response": <PlanResponse>}
    (all items again, plus effects, sounds, notes) or {"event": "error", "detail": ...}."""
    import asyncio
    import json
    import threading

    image_kb = len(req.image_jpeg_b64) * 3 // 4 // 1024 if req.image_jpeg_b64 else 0
    log.info("plan (streamed): %s, %d planes, camera image: %s", _said(req.prompt), len(req.scene.planes),
             f"{image_kb} kB" if image_kb else "none")
    loop = asyncio.get_running_loop()
    queue: asyncio.Queue = asyncio.Queue()

    def emit(event) -> None:
        loop.call_soon_threadsafe(queue.put_nowait, event)

    def on_item(item, interpretation: str) -> None:
        asset = _asset_for(item) if req.generate else None
        emit({"event": "item", "item": item.model_dump(mode="json"), "interpretation": interpretation,
              "asset": asset.model_dump(mode="json") if asset else None})

    def work() -> None:
        try:
            scene_plan = plan_scene(req, on_item=on_item)
            emit({"event": "done", "response": _respond(req, scene_plan).model_dump(mode="json")})
        except PlannerError as e:
            emit({"event": "error", "detail": str(e)})
        except Exception as e:  # noqa: BLE001
            log.exception("plan (streamed) failed")
            emit({"event": "error", "detail": str(e)})
        finally:
            emit(None)

    threading.Thread(target=work, daemon=True, name="plan-stream").start()

    async def events():
        while (event := await queue.get()) is not None:
            yield (json.dumps(event, ensure_ascii=False) + "\n").encode("utf-8")

    return StreamingResponse(events(), media_type="application/x-ndjson")


def _respond(req: PlanRequest, scene_plan) -> PlanResponse:
    """The full answer for a finished plan: logged, objects and sounds requested."""
    if not scene_plan.request:
        log.info("plan: not a request, ignored")
    for it in scene_plan.items:
        p = it.placement
        log.info("  item %s %r x%d: %s/%s target=%s h=%.2f m", it.id, it.label, p.count, p.surface.value,
                 p.layout.value, p.target_id, p.height_m)
    assets: dict[str, AssetInfo] = {}
    if req.generate:
        for item in scene_plan.items:
            assets[item.id] = _asset_for(item)
        if scene_plan.items:
            store.new_scene({a.id for a in assets.values()})
    found: dict[str, SoundInfo] = {}
    if req.generate:
        if scene_plan.ambience and (s := sounds.request(scene_plan.ambience, loop=True)):
            found["ambience"] = s
        for item in scene_plan.items[:]:
            if item.sound and len([k for k in found if k != "ambience"]) < 3:
                if s := sounds.request(item.sound, loop=item.sound_loop):
                    found[item.id] = s
    return PlanResponse(plan=scene_plan, assets=assets, sounds=found, room_light=_room_light(req.image_jpeg_b64))


@app.post("/edit", response_model=EditResponse)
async def edit(req: EditRequest) -> EditResponse:
    """A spoken change to the current scene -> operations for the headset (generation of new looks
    and new objects is started right away)."""
    try:
        out = await run_in_threadpool(edit_scene, req)
    except PlannerError as e:
        raise HTTPException(status_code=502, detail=str(e)) from e
    log.info("edit: %s -> %s", _said(req.utterance), "; ".join(
        f"{op.type.value} {op.targets}" + (f" x{op.scale_factor:g}" if op.type.value == "scale" else "")
        + (f" {op.asset_prompt!r}" if op.asset_prompt else "") for op in out.operations) or "nothing")
    out.room_light = _room_light(req.image_jpeg_b64)
    return out


@app.post("/transcribe")
async def transcribe(request: Request, language: str | None = None) -> dict:
    """Body: a 16-bit PCM WAV of one spoken prompt. `?language=en|de` comes from the keyword the
    headset heard; without it the language is detected. Returns {"text": ...}."""
    if not settings.whisper_model:
        raise HTTPException(503, "speech recognition disabled (WHATIF_WHISPER_MODEL is empty)")
    data = await request.body()
    if len(data) < 44 or len(data) > 10_000_000:
        raise HTTPException(400, "expected a WAV body up to 10 MB")
    # words of the current scene as a hint (URL-encoded header: headers are ASCII only)
    from urllib.parse import unquote

    context = unquote(request.headers.get("x-whatif-context", ""))[:300] or None
    try:
        return await run_in_threadpool(speech.transcribe, data, language, context)
    except ValueError as e:
        raise HTTPException(400, str(e)) from e


@app.post("/assets", response_model=AssetInfo)
def request_asset(req: AssetRequest) -> AssetInfo:
    return store.request(req.prompt, req.want_3d)


@app.get("/assets/{aid}", response_model=AssetInfo)
def asset_status(aid: str) -> AssetInfo:
    info = store.get(aid)
    if info is None:
        raise HTTPException(404, "unknown asset")
    return info


@app.get("/sounds/{sid}.wav")
def sound_file(sid: str) -> FileResponse:
    path = sounds.path(sid)
    if not sid.isalnum() or not path.exists():
        raise HTTPException(404, "not ready")
    return FileResponse(path, media_type="audio/wav")


@app.get("/sounds/{sid}", response_model=SoundInfo)
def sound_status(sid: str) -> SoundInfo:
    info = sounds.get(sid)
    if info is None:
        raise HTTPException(404, "unknown sound")
    return info


@app.get("/credits")
def credits() -> dict:
    """Authors and licences of the real 3D models used (CC-BY asks for attribution)."""
    from . import objaverse

    return {"models": objaverse.credits()}


@app.get("/compare/{aid}/{name}")
def compare_file(aid: str, name: str) -> FileResponse:
    """Results of scripts/compare_meshes.py, to look at them in a browser or 3D viewer."""
    if name not in ("cutout.png", "spar3d.glb", "trellis.glb") or not aid.isalnum():
        raise HTTPException(404, "not found")
    path = settings.data_dir / "compare_meshes" / aid / name
    if not path.exists():
        raise HTTPException(404, "not found")
    return FileResponse(path)


@app.get("/assets/{aid}/billboard.png")
def billboard(aid: str) -> FileResponse:
    path = store.billboard_path(aid)
    if not aid.isalnum() or not path.exists():
        raise HTTPException(404, "not ready")
    return FileResponse(path, media_type="image/png")


@app.get("/assets/{aid}/model.glb")
def model(aid: str) -> FileResponse:
    path = store.model_path(aid)
    if not aid.isalnum() or not path.exists():
        raise HTTPException(404, "not ready")
    return FileResponse(path, media_type="model/gltf-binary")
