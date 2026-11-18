# What if? — architecture

> *"What if this place … had more plants / fewer cars / were flooded / were inhabited by dinosaurs?"*
> An AR tool on the Magic Leap 2 that helps people imagine how their surroundings could change.

The key constraint: **the user can stand anywhere and ask for anything.** So there are no
pre-built scenes or asset libraries. Everything comes from two live sources:

1. **Scene understanding on the headset**: where the floor, walls, ceiling and tables are.
2. **Generative AI on a server**: what to add, and the assets themselves.

## Overview

```
 Magic Leap 2 (Unity, OpenXR)                         Generation server (Python, FastAPI, PC with GPU)
 ─────────────────────────────                        ───────────────────────────────────────────────
 Plane extraction + meshing ─┐
 Head pose, floor estimate  ─┼─► SceneContext ─┐
 RGB camera frame (JPEG)    ─┘                 │
 Prompt (preset / keyboard) ───────────────────┼──► POST /plan ──► Planner (Claude, vision + structured output)
                                               │                    └─► ScenePlan: items + placement rules + effects
                                               │                         │ for every item:
                                               │                         ▼
                                               │                    Asset queue (content-addressed cache)
                                               │                      1. text → image   (SDXL-Turbo, ~1 s)
                                               │                      2. background removal → RGBA cut-out
                                               │                      3. image → 3D GLB  (crossed quads / SF3D / TRELLIS)
 PlacementSolver ◄── ScenePlan ◄───────────────┘
   rules → poses on real surfaces
 Spawn anchored placeholders
 poll GET /assets/{id} ──► billboard ready → show 2D cut-out (faces the viewer)
                       └─► model ready     → swap in 3D model (glTFast)
 EnvironmentEffects: water level, tint, surface paint
```

## Why this split?

| Decision | Reason |
|---|---|
| **Planner outputs *rules*, headset computes *positions*** | Only the headset knows the exact geometry, and it keeps updating. The LLM reasons about meaning ("trees along the curb, 3 m apart"), and deterministic code turns that into geometry. Plans stay small and robust. |
| **One generation per object *kind*, many instances** | Ten trees cost one image + one mesh. Size jitter and random yaw add variety. |
| **2D billboard first, 3D later** | Billboards appear in ~1–2 s, so the user sees a result while 3D is still generating (seconds to minutes). The cut-out is also the *input* for image-to-3D, so no work is wasted. |
| **Normalised asset conventions** | PNG: object stands on the bottom edge. GLB: Y-up, base at y=0, 1 m tall. The client only scales to `height_m`. Any generator can be plugged in as long as it keeps these conventions. |
| **Content-addressed cache** | The same prompt gives the same asset id, so repeated demos are instant and several headsets can share results. |
| **Server on a PC, not on the device** | ML2 cannot run diffusion or 3D-generation models. The server can run locally (RTX 3080 laptop on the same Wi-Fi) or in the cloud. |

## Components

### Headset (Unity 2022.3 LTS, Magic Leap Unity SDK 2.x, OpenXR, AR Foundation 5)

| Script | Role |
|---|---|
| `SceneScanner` | Reads `ARPlaneManager` planes. Uses ML semantic labels (`floor/wall/ceiling/table/seat`), with a geometric fallback. Estimates the floor: lowest large upward plane, then raycast against the mesh, then `head - 1.65 m`. Offers a **virtual ground disc** when nothing was detected. |
| `CameraFrameSource` | Captures a JPEG of the view for the planner. `WebCamFrameSource` is the default; on ML2 you may need an MLCamera-based subclass (see below). |
| `WhatIfClient` | Async HTTP + JSON (Newtonsoft, snake_case). |
| `PlacementSolver` | Layouts: `scatter` (Poisson-like, view-biased), `row` (along the surface's long axis), `edge` (along plane boundaries: curbs, railings, wall bases), `single` (one hero object, best-scored spot in view). Each instance claims a circle of its planned `footprint_m` (largest horizontal size), so objects can't overlap, and big objects keep their edge at least 0.6 m from the user. Respects spacing and max distance. Wall items face out of the wall; ceiling items hang down. Floor objects turn by the plan's `facing`: `random` (plants), `toward_user` (animals, statues; ±25° variation) or `back_to_wall` (sofas, shelves; back to the nearest wall within 2.5 m, else toward the user). |
| `GeneratedObject` | One placed instance: cylindrical billboard → 3D model swap. Soft contact shadow under floor/table objects (round for billboards, sized to the model's footprint for 3D; none on walls or ceilings). On ML2 visible only with segmented dimming, since dark = transparent on the additive display. |
| `AssetLibrary` | Downloads each texture / GLB once and instantiates it many times. |
| `EnvironmentEffects` | `water_level` (transparent plane above the floor), `tint` (tinted inverted sphere), `surface_paint` (via `SurfacePaint`). Reports the current effects to the editor. |
| `CameraView` | Pose + pinhole lens of the camera that took a frame (MLCamera intrinsics + `MLCVCamera.GetFramePose`; the eye camera as a fallback). Projects surfaces into the frame (S-number marks) and unprojects image points into rays. |
| `ImageTargets` | `img:x,y` targets from the planner/editor: ray through the image point against the room mesh (or a detected plane), then straight down from just above the hit for the top of the thing. The solver registers it like an existing object, so on / beside / around work unchanged. |
| `SurfacePaint` | Paint per surface id or kind ("wall" = every wall, also ones detected later); a triangulated (ear-clipped) copy of each plane's outline 6 mm in front of it, parented to the plane, rebuilt when the outline changes. Alpha = strength. Saved in the undo snapshots. |
| `SegmentedDimming` | ML2 only: sets the Magic Leap rendering feature's blend mode to alpha blend (by reflection, so the project compiles without the SDK), transparent black camera background, HDR and post-processing off so the frame keeps alpha. Transparent materials accumulate alpha as coverage. |
| `WhatIfController` | Runs the flow, handles input, and drops stale results when a new round starts. |
| `SceneLighting` | Sets the sun (0.7) and a flat, dim ambient light at start (`WhatIfSettings` ▸ Lighting), and adds a directional light if the scene has none. SPAR3D's shading-free colours looked washed out under Unity's default sun + sky lighting, and lit models need a light on the headset. |
| `LoadingEffect` | Particles at each placed spot while it is generated: a rising column over the footprint until the cut-out appears, a shimmer until the 3D model replaces it, a burst on each arrival. Bright additive colours (additive display). URP particle shader is found at runtime: add *Universal Render Pipeline/Particles/Unlit* to *Always Included Shaders* for headset builds. |
| `VoiceCommandListener` | Microphone → offline Vosk keyword spotters for "what if" (English model) and "was wäre wenn" (German model) on a worker thread. A keyword only counts as consecutive words with ≤0.3 s gaps: a two-phrase grammar alone produced false German hits on English speech, at confidence 1.0. Recording starts 0.3 s before the spoken keyword (Vosk word timing), ends after 1 s of silence → `POST /transcribe?language=en|de` → only a transcript that starts with a keyword becomes a prompt. Only audio after a keyword leaves the device. `VoskModelLocator` unpacks the models from the APK on Android. |
| `WhatIfController.Editing` | Voice edits: routes sentences ("What if" = new scenario, else edit), sends `POST /edit` with the objects (ids like `chair#2`, group, label, description, bearing/distance/size), the gazed object and the last changed ids, then applies the returned operations (move, move_to via the placement solver, scale, rotate, face, restyle via `BeginReplace` + regeneration, delete = deactivate, add, undo from snapshots). Moving replaces AR anchors. Edits run one after another. |
| `GazeTracker` | Eye gaze from OpenXR's Eye Gaze Interaction device (head direction as fallback); records the object nearest the gaze ray every frame so a sentence can be matched to what was looked at during it and the second before. |
| `HeadsetHud` | Small status text ~18° below eye level that lazily follows your view; results fade out after 6 s, work-in-progress messages stay. |
| `EditorFlyCamera`, `EditorGround` | Editor-only testing aids: keyboard walking, and a 1 m grid floor at the assumed ground height. Both stay off when a headset is connected. |
| `Editor/WhatIfSetup` | Menu **What If ▸ Add To Open Scene** wires everything into the scene. |

### Server (`server/whatif`)

| Module | Role |
|---|---|
| `planner.py` | Claude (`claude-opus-5-5`, adaptive thinking, `effort=low` for latency) with **structured outputs** (`ScenePlan` pydantic model). Takes the camera image + surface list. Server-side refusal fallback is enabled. A `mock` planner (keyword rules) lets you work offline. Colour changes of the real room become `surface_paint` effects on detected surfaces (S-numbers or kinds); paint for undetected kinds is dropped with a hint to look at them. |
| `generators.py` | Image providers: `mock`, `sdxl_turbo` (diffusers, local GPU). Cut-out: BiRefNet (PyTorch fp16) or a white-key fallback. Stray props are dropped, the image is regenerated with a new seed if the object touches the border, then it's trimmed to the base. Mesh providers: `triposr` (in-process on the GPU, ~3 s, simplified to 20k triangles), `crossquad` (instant crossed planes), `command` (any external image-to-3D CLI, normalised afterwards), `none`. |
| `assets.py` | Content-addressed store with two GPU queues (cut-outs first, 3D separately). Status flow: `queued → running → billboard_ready → done`. A failed 3D step still leaves the billboard. |
| `speech.py` | `POST /transcribe`: faster-whisper "small", int8 on the CPU (keeps the GPU for image/3D models), ~1.5 s per sentence. Language = the keyword's language from the headset, unless Whisper is >80 % sure of the other allowed language (`WHATIF_WHISPER_LANGUAGES=en,de`). |
| `editor.py` | `POST /edit`: Claude turns a sentence + the scene's objects into operations (structured output; every field required with neutral values - optional fields made the API reject the schema as "too complex"). Checks targets exist, expands groups ("all chairs"), falls back to the gazed / last changed objects for "it", maps S-numbers, starts generation for restyles and additions. `paint` operations colour real surfaces (S-numbers or kinds); the current effects come with the request so "darker" works. |
| `main.py` | `POST /transcribe`, `POST /plan`, `POST /assets`, `GET /assets/{id}`, `GET /assets/{id}/billboard.png`, `GET /assets/{id}/model.glb`, `GET /health`. |

## Magic Leap 2 specifics and risks

- **Additive display.** Black renders as transparent and dark colours look faint. Generated
  photos of dark objects (tree trunks, asphalt) will look washed out. Enable **segmented
  dimming** in the Magic Leap OpenXR settings (`SegmentedDimming` then sets everything else up),
  so objects block real light behind them. This matters a lot for the "realistic" scenarios.
- **Outdoors.** Your pitch photo (Baumwall, Hamburg) is outdoors. In bright sunlight, ML2
  depth sensing, meshing and plane extraction get unreliable, and the display competes with
  the sun. The app degrades on purpose: no planes → virtual ground at head height − 1.65 m.
  For an outdoor demo, choose an overcast day or shade, and use the global dimmer.
- **Plane extraction range** is a few metres. Large outdoor layouts (trees 10 m away) mostly
  land on the virtual ground. That is fine on flat streets, but the objects won't follow slopes.
- **Camera access.** The RGB camera on ML2 is normally accessed through the Magic Leap camera
  API (`MLCamera`), not `WebCamTexture`. If `WebCamFrameSource` gets no device on the headset,
  write a `CameraFrameSource` subclass based on the SDK's camera sample. Until then the planner
  works without an image, but the plans are less specific to the place.
- **Permissions.** `com.magicleap.permission.SPATIAL_MAPPING` (planes, meshing) and
  `android.permission.CAMERA`, requested at start-up.
- **Occlusion.** Add an `ARMeshManager` with an occlusion material (depth-only) so real walls
  and people hide virtual objects. The mesh also gets colliders for the floor raycast.

## Latency budget (target: first visible result < 5 s)

| Step | Estimate |
|---|---|
| Scan + JPEG capture | < 0.3 s |
| Planner (Claude, effort low, with image) | ~2–6 s |
| Image per kind (SDXL-Turbo, 512², 4 steps, RTX 3080 Laptop) + BiRefNet | ~1.1 s per attempt, 1–3 attempts (measured) |
| Crossed-quad GLB | < 0.1 s |
| TripoSR image-to-3D + simplification (RTX 3080 Laptop) | ~3–4 s per kind (measured) |

Measured end to end on the RTX 3080 Laptop (mock planner, models preloaded): first billboard
after 1–2 s, all objects of an idea after 3–7 s, cached ideas instantly. The planner rows are
still estimates; measure them.

### Lessons from the GPU setup

- **Background removal decides the "more plants" scenario.** u2net and isnet treat dense
  foliage as background and cut away tree crowns; BiRefNet keeps it.
- **VRAM:** BiRefNet's ONNX export needs > 6 GB in fp32 and doesn't fit next to SDXL-Turbo on
  16 GB, so it runs in PyTorch fp16 (~1 GB). The model code comes from the authors' Hugging Face
  repo (`trust_remote_code`), pinned to a reviewed revision.
- **SDXL-Turbo ignores negative prompts** (no guidance), and with "fully visible, nothing cut off"
  it still fills the frame. Asking for a *small* object in a *large empty white studio* gave
  complete objects (6/8 vs 0/8 for trees and shrubs).
- **TripoSR instead of Stable Fast 3D / TRELLIS.2:** no CUDA toolkit or Hugging Face login is needed,
  and it fits in the ~4 GB left next to the image models. Two fixes were needed: scikit-image
  replaces the compiled `torchmcubes`, and the checkpoint's old ViT parameter names are renamed
  for current transformers versions. Raw meshes have up to 300k triangles; they are simplified to
  20k, with vertex colours copied from the nearest original vertex.
- **Dark models:** glTF's default material is fully metallic, so a model without a material renders
  almost black in Unity. Generated colours were also sRGB, while glTF vertex colours are linear.
  Every model now gets a non-metallic material marked `KHR_materials_unlit`, and colours are
  converted to linear. Unlit fits here because the colours come from the image and already contain
  shading, and the Magic Leap 2 has no real lighting to match. It also makes 3D models exactly as
  bright as the billboards.
- **Texture baking** (`WHATIF_MESH_TEXTURE_SIZE=1024`) bakes TripoSR's colour field into a texture
  on the simplified mesh. It is somewhat sharper, but costs ~5 s and ~2 MB more per model, because
  TripoSR's colour field itself is coarse. Off by default; the next real quality step is a better
  image-to-3D model, not more texture resolution.
- **Model conventions:** every generated GLB is Y-up, 1 m tall, base at y=0, and its *front* (the
  side seen in the source image) faces +Z. TripoSR's own demo transform leaves the front at -Z, so the
  server turns models 180°. glTFast mirrors X on import, so +Z stays the front in Unity. Meshes from
  scikit-image's marching cubes come out inside-out; they are flipped by checking whether most faces
  point away from the centre (a signed-volume check does not work, because the meshes are not closed).
- **SPAR3D** (Stability AI) is much sharper than TripoSR. Made to run on Python 3.14 /
  transformers 5 / Windows without the CUDA Toolkit by: a CPU-only texture baker wrapped so it
  accepts GPU tensors; stand-ins for `transparent_background` (unused, BiRefNet makes the cut-outs);
  and two helpers transformers 5 removed (`find_pruneable_heads_and_indices`, `get_head_mask`),
  restored before SPAR3D loads. Its output is Y-up with the image side at -Z, so it is turned 180°.
  It needs ~10 GB in normal mode; storing weights in bfloat16 fails (some code runs in fp32) and
  still would not fit, so `GpuArbiter` swaps it with SDXL-Turbo + BiRefNet (~3–4 s per swap). The
  3D queue waits until all cut-outs are done, so a request swaps once; the swap back happens while idle.
- **Tilted models:** image-to-3D models inherit the camera elevation of the source photo (taken from
  slightly above), so tables stood on two legs and sofas leaned by 5–15°. `level_and_clean` picks the
  model's most likely stable resting pose (trimesh stable poses on the convex hull of its main parts)
  within 25° and applies only that tilt, keeping the facing. Parts are found by vertex *position*,
  because textured meshes split vertices at UV seams. Crumbs under 1.5% of the model and loose bits
  below the base are removed. Known outlier: objects with a lump under them (a "floating" crate)
  can be tipped onto it.
- **Measured end to end with 3D:** all cut-outs of an idea within 2–8 s, 3D models within 6–14 s,
  peak 13.5 GB VRAM.
- **CUDA versions must match the driver:** driver 551 supports CUDA 12.x only. Builds for
  CUDA 13 silently fall back to the CPU. If the planner is the bottleneck, try a faster model via
`WHATIF_PLANNER_MODEL` or stream the plan item by item.

## Roadmap

1. **MVP (this scaffold):** presets + keyboard prompt, billboards, crossed-quad pseudo-3D,
   water effect, offline mock mode.
2. **Real 3D:** plug Stable Fast 3D or TRELLIS.2 into `WHATIF_MESH_COMMAND`. Optionally a
   persistent model server to avoid start-up cost per asset.
3. **Better grounding:** project placements onto the world mesh (raycasts) instead of only
   planes. Use the camera image + depth for semantic regions ("road", "railing") so the
   planner can say "along the railing" and the solver can find it.
4. **Diminished reality** ("fewer cars"): segment cars in the camera frame and cover them
   with inpainted patches projected onto the mesh. This is hard on an additive display, so
   start with dimming plus overlaid replacement content.
5. **Voice input:** use ML voice intents or dictation for free-form "What if …".
6. **Comparison and sharing:** before/after toggle, capture to the photo gallery, keep
   scenarios as spatial anchors to revisit with others (participatory urban planning).
7. **Realism slider:** the planner already returns `realistic | playful`. Expose it as a user
   choice and use it in the prompt style and the asset shading.
