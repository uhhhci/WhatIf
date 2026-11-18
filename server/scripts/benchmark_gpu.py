"""Generate a few assets with the configured GPU providers and time each step.

Usage:  python scripts/benchmark_gpu.py [output_dir]
First run downloads SDXL-Turbo (~7 GB) and the rembg model (~170 MB).
"""

import os
import sys
import time
from pathlib import Path

os.environ.setdefault("WHATIF_IMAGE_PROVIDER", "sdxl_turbo")
os.environ.setdefault("WHATIF_MESH_PROVIDER", "crossquad")
os.environ.setdefault("WHATIF_REMOVE_BG", "1")
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from whatif.generators import CrossQuadMeshProvider, generate_cutout, image_provider  # noqa: E402

PROMPTS = [
    "a young linden tree with a full green crown, realistic photo",
    "a lush flowering shrub with purple and white blossoms, realistic photo",
    "a city bicycle parked in a metal bike rack, realistic photo",
    "a friendly brachiosaurus dinosaur, full body side view, realistic render",
]


def main() -> None:
    out = Path(sys.argv[1] if len(sys.argv) > 1 else "data/benchmark")
    out.mkdir(parents=True, exist_ok=True)

    t = time.perf_counter()
    provider = image_provider()
    print(f"load image model: {time.perf_counter() - t:.1f} s", flush=True)

    t = time.perf_counter()
    generate_cutout("a wooden chair")
    print(f"warm-up (image + background model): {time.perf_counter() - t:.1f} s", flush=True)

    mesher = CrossQuadMeshProvider()
    for i, prompt in enumerate(PROMPTS):
        t0 = time.perf_counter()
        cut, tries = generate_cutout(prompt)
        t1 = time.perf_counter()
        cut.save(out / f"{i}_cutout.png")
        mesher.generate(out / f"{i}_cutout.png", out / f"{i}.glb")
        t2 = time.perf_counter()
        print(f"[{i}] cut-out {t1 - t0:.2f} s ({tries} attempt(s)) | glb {t2 - t1:.2f} s | "
              f"{cut.width}x{cut.height}  {prompt}", flush=True)

    import torch

    print(f"peak VRAM: {torch.cuda.max_memory_allocated() / 2**30:.1f} GiB")


if __name__ == "__main__":
    main()
