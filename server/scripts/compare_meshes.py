"""Compare TRELLIS.2 with the current 3D models (SPAR3D) on the same cut-outs.

Takes objects the server already made (cut-out + SPAR3D model), sends each cut-out to the TRELLIS
worker and stores both models side by side, with the time TRELLIS needed. It also estimates which
turn around the vertical axis lines TRELLIS's models up with ours (front = +Z), for
WHATIF_TRELLIS_ROTATIONS.

    cd ~/whatif/server && .venv/bin/python scripts/compare_meshes.py --recent 6
    cd ~/whatif/server && .venv/bin/python scripts/compare_meshes.py --prompt eiffel --prompt tree

Results: data/compare_meshes/<asset>/ (cutout.png, spar3d.glb, trellis.glb), also served at
http://<server>:8000/compare/<asset>/<file> while the server runs.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import numpy as np  # noqa: E402

from whatif.config import settings  # noqa: E402
from whatif.generators import TrellisMeshProvider, normalize_glb, trellis_reachable  # noqa: E402


def points(glb: Path, n: int = 3000) -> np.ndarray:
    import trimesh

    mesh = trimesh.load(glb, force="mesh")
    pts, _ = trimesh.sample.sample_surface(mesh, n, seed=1)
    return pts - pts.mean(axis=0)


def chamfer(a: np.ndarray, b: np.ndarray) -> float:
    from scipy.spatial import cKDTree

    return float(cKDTree(b).query(a)[0].mean() + cKDTree(a).query(b)[0].mean())


def best_turn(ours: Path, theirs: Path) -> tuple[int, dict]:
    """The turn around +Y (0/90/180/270 deg) that brings TRELLIS's model closest to ours."""
    a, b = points(ours), points(theirs)
    scores = {}
    for deg in (0, 90, 180, 270):
        r = np.radians(deg)
        rot = np.array([[np.cos(r), 0, np.sin(r)], [0, 1, 0], [-np.sin(r), 0, np.cos(r)]])
        scores[deg] = chamfer(a, b @ rot.T)
    return min(scores, key=scores.get), scores


def pick(root: Path, recent: int, prompts: list[str]) -> list[Path]:
    dirs = [d for d in root.iterdir() if (d / "billboard.png").exists() and (d / "model.glb").exists()]
    if prompts:
        def text(d):
            try:
                return json.loads((d / "meta.json").read_text(encoding="utf-8")).get("prompt", "").lower()
            except (OSError, ValueError):
                return ""
        dirs = [d for d in dirs if any(p.lower() in text(d) for p in prompts)]
    dirs.sort(key=lambda d: (d / "model.glb").stat().st_mtime, reverse=True)
    return dirs[:recent]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--recent", type=int, default=6, help="how many of the latest objects")
    ap.add_argument("--prompt", action="append", default=[], help="only objects whose description contains this")
    args = ap.parse_args()
    if not trellis_reachable():
        sys.exit(f"The TRELLIS worker isn't reachable at {settings.trellis_url} - start it first.")
    root = settings.data_dir / "assets"
    out_root = settings.data_dir / "compare_meshes"
    chosen = pick(root, args.recent, args.prompt)
    if not chosen:
        sys.exit("No finished objects with a 3D model found (try without --prompt).")
    provider = TrellisMeshProvider()
    turns = []
    for d in chosen:
        prompt = json.loads((d / "meta.json").read_text(encoding="utf-8")).get("prompt", "?")
        out = out_root / d.name
        out.mkdir(parents=True, exist_ok=True)
        shutil.copy(d / "billboard.png", out / "cutout.png")
        shutil.copy(d / "model.glb", out / "spar3d.glb")
        t = time.perf_counter()
        try:
            provider.generate(d / "billboard.png", out / "trellis.glb")
        except Exception as e:  # noqa: BLE001
            print(f"{d.name}  FAILED  {e}  ({prompt[:60]})")
            continue
        took = time.perf_counter() - t
        turn, scores = best_turn(out / "spar3d.glb", out / "trellis.glb")
        turns.append(turn)
        print(f"{d.name}  {took:5.1f} s  best turn y:{turn}  ({prompt[:70]})")
    if turns:
        common = max(set(turns), key=turns.count)
        print(f"\nModels in {out_root}")
        print(f"Most objects line up with WHATIF_TRELLIS_ROTATIONS=y:{common}"
              f"{' (no setting needed)' if common == 0 else ''} - {turns.count(common)} of {len(turns)} agree.")


if __name__ == "__main__":
    main()
