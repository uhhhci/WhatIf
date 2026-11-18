"""Runtime configuration, read from environment variables (or a .env file)."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path


def _load_dotenv(path: Path) -> None:
    """Later lines win (a setting appended with `echo ... >> .env` overrides an earlier one);
    real environment variables win over the file."""
    if not path.exists():
        return
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip('"').strip("'")
    for key, value in values.items():
        os.environ.setdefault(key, value)


_load_dotenv(Path(__file__).resolve().parent.parent / ".env")


def _env(name: str, default: str) -> str:
    return os.environ.get(name, default)


@dataclass(frozen=True)
class Settings:
    # planner (also the editor): "claude" (Anthropic API, needs credentials), "local" (an open model
    # served by vLLM on this machine, camera pictures stay in the network) or "mock" (keyword rules)
    planner: str = field(default_factory=lambda: _env("WHATIF_PLANNER", "claude"))
    # local planner: vLLM's OpenAI-compatible endpoint and the model it serves
    local_url: str = field(default_factory=lambda: _env("WHATIF_LOCAL_URL", "http://127.0.0.1:8001/v1"))
    local_model: str = field(default_factory=lambda: _env("WHATIF_LOCAL_MODEL", "Qwen/Qwen3.5-35B-A3B-GPTQ-Int4"))
    # Moving parts are cut out of the 3D model (tail, wings): the cuts crack the mesh (seen on an
    # elephant), so off unless WHATIF_MOVING_PARTS=1. A tint veils the whole view (looked like a grey
    # plane cut by the wall occluders), so off unless WHATIF_TINT=1.
    moving_parts: bool = field(default_factory=lambda: _env("WHATIF_MOVING_PARTS", "0") == "1")
    tint: bool = field(default_factory=lambda: _env("WHATIF_TINT", "0") == "1")
    # the local model's context (vLLM --max-model-len): question + answer must fit into it
    local_context: int = field(default_factory=lambda: int(_env("WHATIF_LOCAL_CONTEXT", "16384")))
    local_timeout_s: float = field(default_factory=lambda: float(_env("WHATIF_LOCAL_TIMEOUT_S", "120")))
    # Sentences without "What if" while nothing is shown may be anyone's conversation; they are judged
    # only by the local planner unless this is 1 (then they go to Claude too).
    natural_requests_online: bool = field(default_factory=lambda: _env("WHATIF_NATURAL_ONLINE", "0") == "1")
    local_temperature: float = field(default_factory=lambda: float(_env("WHATIF_LOCAL_TEMPERATURE", "0.4")))
    planner_model: str = field(default_factory=lambda: _env("WHATIF_PLANNER_MODEL", "claude-opus-5-5"))
    # The user is standing in AR waiting for the result, so default to low effort.
    planner_effort: str = field(default_factory=lambda: _env("WHATIF_PLANNER_EFFORT", "low"))
    # Voice edits should feel snappy: "low" by default (raise for harder edits).
    edit_effort: str = field(default_factory=lambda: _env("WHATIF_EDIT_EFFORT", "low"))
    planner_fallbacks: bool = field(default_factory=lambda: _env("WHATIF_PLANNER_FALLBACKS", "1") == "1")

    # image: "mock" (placeholder shapes) or "sdxl_turbo" (local GPU via diffusers)
    image_provider: str = field(default_factory=lambda: _env("WHATIF_IMAGE_PROVIDER", "mock"))
    image_model: str = field(default_factory=lambda: _env("WHATIF_IMAGE_MODEL", "stabilityai/sdxl-turbo"))
    image_steps: int = field(default_factory=lambda: int(_env("WHATIF_IMAGE_STEPS", "4")))
    # Re-generate (new seed) when the object touches the image border; ~1 s per attempt.
    image_attempts: int = field(default_factory=lambda: int(_env("WHATIF_IMAGE_ATTEMPTS", "3")))
    # Candidates generated before picking the one CLIP finds closest to the prompt (and least cut
    # off). 1 = old behaviour (first uncut image wins). More = better match for unusual requests
    # ("a blue giraffe with a hat") at ~0.3-1 s per candidate. 0 disables CLIP entirely.
    # 1 = every object becomes a 3D model and the planner avoids flat things (posters, wall
    # panels, ivy); 0 = flat things may stay 2D cut-outs (the old behaviour)
    only_3d: bool = field(default_factory=lambda: _env("WHATIF_ONLY_3D", "1") == "1")
    # Privacy: whether what users say (transcripts, prompts, edit sentences) appears in the console
    # log. On for now (debugging); WHATIF_LOG_CONTENT=0 logs only word counts. Audio and camera
    # pictures are never stored, whatever this says.
    log_user_content: bool = field(default_factory=lambda: _env("WHATIF_LOG_CONTENT", "1") == "1")
    # Object library: a new object whose description means practically the same as a finished one
    # (CLIP text similarity >= threshold) reuses it instead of generating again.
    library: bool = field(default_factory=lambda: _env("WHATIF_LIBRARY", "1") == "1")
    # real pictures from the web (public domain / CC0 only; people: historical figures only)
    web_images: bool = field(default_factory=lambda: _env("WHATIF_WEB_IMAGES", "1") == "1")
    web_candidates: int = field(default_factory=lambda: int(_env("WHATIF_WEB_CANDIDATES", "2")))
    web_timeout_s: float = field(default_factory=lambda: float(_env("WHATIF_WEB_TIMEOUT", "6")))
    web_bonus: float = field(default_factory=lambda: float(_env("WHATIF_WEB_BONUS", "0.02")))
    library_threshold: float = field(default_factory=lambda: float(_env("WHATIF_LIBRARY_THRESHOLD", "0.95")))
    # Generated sounds (ambience + a few object sounds) with Stable Audio Open on the GPU.
    sounds: bool = field(default_factory=lambda: _env("WHATIF_SOUNDS", "1") == "1")
    sound_seconds: float = field(default_factory=lambda: float(_env("WHATIF_SOUND_SECONDS", "8")))
    sound_steps: int = field(default_factory=lambda: int(_env("WHATIF_SOUND_STEPS", "50")))
    image_candidates: int = field(default_factory=lambda: int(_env("WHATIF_IMAGE_CANDIDATES", "1")))
    # Background removal: "birefnet" (keeps foliage; ~1.3 s on GPU) or any rembg model name
    # such as "isnet-general-use" / "u2net" (faster on CPU, but they cut away tree crowns).
    matting_model: str = field(default_factory=lambda: _env("WHATIF_MATTING_MODEL", "birefnet"))
    remove_background: bool = field(default_factory=lambda: _env("WHATIF_REMOVE_BG", "1") == "1")

    # mesh: "none", "crossquad" (two crossed textured quads, instant), "triposr" (GPU) or "command"
    mesh_provider: str = field(default_factory=lambda: _env("WHATIF_MESH_PROVIDER", "crossquad"))
    # For "command": a shell command with {input} (PNG path) and {output_dir}; it must write one .glb.
    # Example for Stable Fast 3D:  python C:/tools/stable-fast-3d/run.py {input} --output-dir {output_dir}
    mesh_command: str = field(default_factory=lambda: _env("WHATIF_MESH_COMMAND", ""))
    triposr_dir: str = field(
        default_factory=lambda: _env("WHATIF_TRIPOSR_DIR", str(Path(__file__).resolve().parent.parent / "third_party" / "TripoSR"))
    )
    # Triangle budget per generated model; the headset renders many instances at once.
    mesh_max_faces: int = field(default_factory=lambda: int(_env("WHATIF_MESH_MAX_FACES", "20000")))
    # Filler objects (many small things in a full room) get lighter models, so a scene with 100+
    # objects stays smooth on the headset. SPAR3D needs the gpytoolbox package for it.
    mesh_lite_faces: int = field(default_factory=lambda: int(_env("WHATIF_MESH_LITE_FACES", "5000")))
    # coat pieces (moss tufts, leaves, ivy strands): up to ~150 copies, so a very light model each
    mesh_tiny_faces: int = field(default_factory=lambda: int(_env("WHATIF_MESH_TINY_FACES", "1500")))
    # Baked texture size for 3D models (0 = per-vertex colours). 1024 is a little sharper but adds
    # ~5 s and ~2 MB per model; TripoSR's own colour field limits the detail either way.
    mesh_texture_size: int = field(default_factory=lambda: int(_env("WHATIF_MESH_TEXTURE_SIZE", "0")))
    # Unlit models look exactly like the billboards they came from (colours already contain shading).
    mesh_unlit: bool = field(default_factory=lambda: _env("WHATIF_MESH_UNLIT", "1") == "1")
    spar3d_dir: str = field(
        default_factory=lambda: _env("WHATIF_SPAR3D_DIR", str(Path(__file__).resolve().parent.parent / "third_party" / "SPAR3D"))
    )
    spar3d_texture_size: int = field(default_factory=lambda: int(_env("WHATIF_SPAR3D_TEXTURE_SIZE", "1024")))
    # SPAR3D needs ~10.5 GB VRAM, ~7 GB in low-VRAM mode (slower). SDXL-Turbo + BiRefNet use ~8 GB.
    # Default: swap between image models and SPAR3D on the GPU (~7 s per model). Low-VRAM mode
    # keeps everything resident instead but takes ~30-40 s per model.
    spar3d_low_vram: bool = field(default_factory=lambda: _env("WHATIF_SPAR3D_LOW_VRAM", "0") == "1")
    # 0 = GPU with enough memory for everything (~20 GB): image models and SPAR3D stay loaded,
    # no swapping, cut-outs and 3D models are made at the same time.
    gpu_swap: bool = field(default_factory=lambda: _env("WHATIF_GPU_SWAP", "1") == "1")
    # Rotations (axis, degrees) applied to SPAR3D output so that up = +Y and front = +Z.
    # Checked visually: SPAR3D is Y-up with the image side facing -Z, so it needs a half turn.
    spar3d_rotations: tuple = field(default_factory=lambda: tuple(
        (r.split(":")[0], float(r.split(":")[1]))
        for r in _env("WHATIF_SPAR3D_ROTATIONS", "y:180").split(",") if r.strip()
    ))
    # Real 3D models from Objaverse for prominent, still objects (whatif/objaverse.py; build the index
    # once with scripts/build_objaverse_index.py). Used only when a model clearly fits.
    objaverse: bool = field(default_factory=lambda: _env("WHATIF_OBJAVERSE", "0") == "1")
    objaverse_min_score: float = field(default_factory=lambda: float(_env("WHATIF_OBJAVERSE_MIN_SCORE", "0.30")))
    # a real model must also look like the picture generated for the object (CLIP image similarity of its
    # preview to the cut-out). Measured: right models 0.79-0.85; a cartoon taxi in space for "a yellow
    # taxi" 0.74, a leather office chair for "a wooden office chair" 0.75
    objaverse_min_picture: float = field(default_factory=lambda: float(_env("WHATIF_OBJAVERSE_MIN_PICTURE", "0.76")))
    # things that move are generated as a side view, previews mostly show them from the front at an
    # angle: good dog models scored 0.67-0.71 against a dog's side view. The vision model then has the
    # last word (right thing, realistic) - a meerkat scored like the dogs
    # prominent objects (not fillers) only from models others found good: at least this many Sketchfab
    # likes, or a staff pick (seen: a hobby dog model, blobby from the front). 0 = no minimum
    objaverse_min_likes: int = field(default_factory=lambda: int(_env("WHATIF_OBJAVERSE_MIN_LIKES", "5")))
    objaverse_min_picture_mover: float = field(default_factory=lambda: float(_env("WHATIF_OBJAVERSE_MIN_PICTURE_MOVER", "0.66")))
    objaverse_candidates: int = field(default_factory=lambda: int(_env("WHATIF_OBJAVERSE_CANDIDATES", "12")))
    # low-poly models (a 949-triangle piano) look poorer than a generated one: not below this
    objaverse_min_faces: int = field(default_factory=lambda: int(_env("WHATIF_OBJAVERSE_MIN_FACES", "3000")))
    objaverse_max_faces: int = field(default_factory=lambda: int(_env("WHATIF_OBJAVERSE_MAX_FACES", "80000")))
    # fillers (rocks, logs, crates) come in numbers: lighter real models only
    objaverse_filler_max_faces: int = field(default_factory=lambda: int(_env("WHATIF_OBJAVERSE_FILLER_MAX_FACES", "20000")))
    objaverse_max_mb: float = field(default_factory=lambda: float(_env("WHATIF_OBJAVERSE_MAX_MB", "15")))
    objaverse_texture: int = field(default_factory=lambda: int(_env("WHATIF_OBJAVERSE_TEXTURE", "1024")))
    # licences that only ask for attribution (no "no derivatives": models are scaled and textures shrunk)
    objaverse_licences: str = field(default_factory=lambda: _env("WHATIF_OBJAVERSE_LICENCES", "cc0,by"))
    # TRELLIS.2 (scripts/trellis_worker.py, its own conda environment): WHATIF_MESH_PROVIDER=trellis
    # for every object, or WHATIF_HERO_MESH_PROVIDER=trellis for prominent objects only (fillers keep
    # the main provider, e.g. SPAR3D). Detail: 512 (fast) | 1024 | 1024_cascade | 1536_cascade.
    hero_mesh_provider: str = field(default_factory=lambda: _env("WHATIF_HERO_MESH_PROVIDER", ""))
    trellis_url: str = field(default_factory=lambda: _env("WHATIF_TRELLIS_URL", "http://127.0.0.1:8019"))
    trellis_type: str = field(default_factory=lambda: _env("WHATIF_TRELLIS_TYPE", "512"))
    trellis_faces: int = field(default_factory=lambda: int(_env("WHATIF_TRELLIS_FACES", "40000")))
    trellis_texture: int = field(default_factory=lambda: int(_env("WHATIF_TRELLIS_TEXTURE", "1024")))
    # Rotations (axis:degrees) so that up = +Y and front = +Z; scripts/compare_meshes.py estimates them
    trellis_rotations: tuple = field(default_factory=lambda: tuple(
        (r.split(":")[0], float(r.split(":")[1]))
        for r in _env("WHATIF_TRELLIS_ROTATIONS", "").split(",") if r.strip()
    ))
    # Stand generated models upright (they inherit the source photo's camera tilt) and drop loose
    # fragments below them.
    mesh_auto_level: bool = field(default_factory=lambda: _env("WHATIF_MESH_AUTO_LEVEL", "1") == "1")
    triposr_resolution: int = field(default_factory=lambda: int(_env("WHATIF_TRIPOSR_RESOLUTION", "256")))
    triposr_chunk_size: int = field(default_factory=lambda: int(_env("WHATIF_TRIPOSR_CHUNK_SIZE", "8192")))
    mesh_timeout_s: int = field(default_factory=lambda: int(_env("WHATIF_MESH_TIMEOUT_S", "300")))

    # Voice prompts: faster-whisper model ("" disables /transcribe). "auto": large-v3-turbo on a GPU
    # (much better with German and names, a short sentence in well under a second, ~1.6 GB), small
    # on the CPU (large models are too slow there). Or name one: small, medium, large-v3, ...
    whisper_model: str = field(default_factory=lambda: _env("WHATIF_WHISPER_MODEL", "auto"))
    # auto = the GPU if there is one (much faster), else the CPU; or force "cuda" / "cpu".
    # Names of the objects shown as hints for speech recognition: they become more likely, nothing
    # else less possible (anything unexpected can still be said). 0 turns the hints off.
    whisper_hints: bool = field(default_factory=lambda: _env("WHATIF_WHISPER_HINTS", "1") == "1")
    whisper_device: str = field(default_factory=lambda: _env("WHATIF_WHISPER_DEVICE", "auto"))
    # Languages a spoken prompt may be in (comma separated, Whisper codes). The language is detected
    # per sentence but restricted to these, so short clips don't drift into other languages.
    whisper_languages: str = field(default_factory=lambda: _env("WHATIF_WHISPER_LANGUAGES", "en,de"))

    data_dir: Path = field(
        default_factory=lambda: Path(_env("WHATIF_DATA_DIR", str(Path(__file__).resolve().parent.parent / "data")))
    )
    # GPU work is serialised; raise only if the providers are remote APIs.
    workers: int = field(default_factory=lambda: int(_env("WHATIF_WORKERS", "1")))


settings = Settings()
