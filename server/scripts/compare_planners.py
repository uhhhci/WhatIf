"""Compare the local planner with Claude on the same requests, side by side.

Usage (on the GPU server, with vLLM running and ANTHROPIC_API_KEY in .env):
    .venv/bin/python scripts/compare_planners.py [--image photo.jpg] [--only local|claude]

Every prompt runs through both planners with the same room (a typical office: floor, four walls,
a desk, a ceiling). With --image, that picture is sent as the camera frame for all prompts (use a
photo you took for this; nothing is kept). Nothing is generated, only planned. The report goes to
data/compare_planners/<time>.md: time, interpretation, objects with their placement, effects and
errors per planner, so the quality can be judged by reading.
"""

from __future__ import annotations

import argparse
import base64
import os
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines() if (ROOT / ".env").exists() else []:
    line = line.strip()
    if line and not line.startswith("#") and "=" in line:
        k, v = line.split("=", 1)
        os.environ.setdefault(k.strip(), v.strip())

from whatif.planner import PlannerError, plan_scene  # noqa: E402
from whatif.schemas import PlanRequest  # noqa: E402

PROMPTS = [
    # single objects
    "What if there was a giraffe here?",
    "Was wäre, wenn auf dem Schreibtisch eine Katze sitzen würde?",
    "What if a bright blue giraffe wearing a red top hat stood next to me?",
    # environments
    "What if this room was a medieval castle hall?",
    "Was wäre, wenn hier ein Dschungel wäre?",
    "What if this office was flooded?",
    # colour and theme rules
    "What if the walls were painted dark green?",
    "What if this place had a stone wall?",
    # odd and playful
    "What if this room was a cozy reading corner for a giant?",
    "Was wäre, wenn hier Poster und Efeu an den Wänden wären?",
]

ROOM = {
    "head_height_m": 1.6,
    "planes": [
        {"id": "floor", "label": "floor", "width_m": 5.0, "height_m": 4.0, "distance_m": 0.5, "height_rel_head_m": -1.6},
        {"id": "w-front", "label": "wall", "width_m": 5.0, "height_m": 2.8, "distance_m": 2.0, "height_rel_head_m": -0.2, "bearing_deg": 0},
        {"id": "w-right", "label": "wall", "width_m": 4.0, "height_m": 2.8, "distance_m": 2.5, "height_rel_head_m": -0.2, "bearing_deg": 90},
        {"id": "w-left", "label": "wall", "width_m": 4.0, "height_m": 2.8, "distance_m": 2.5, "height_rel_head_m": -0.2, "bearing_deg": -90},
        {"id": "w-back", "label": "wall", "width_m": 5.0, "height_m": 2.8, "distance_m": 2.0, "height_rel_head_m": -0.2, "bearing_deg": 180},
        {"id": "desk", "label": "table", "width_m": 1.6, "height_m": 0.8, "distance_m": 1.2, "height_rel_head_m": -0.85,
         "bearing_deg": -20, "height_above_floor_m": 0.75},
        {"id": "ceiling", "label": "ceiling", "width_m": 5.0, "height_m": 4.0, "distance_m": 0.5, "height_rel_head_m": 1.2},
    ],
}


def describe(plan) -> list[str]:
    lines = [f"*{plan.interpretation}*" + (f" — note: {plan.note_for_user}" if plan.note_for_user else "")]
    for it in plan.items:
        p = it.placement
        target = f", target {p.target_id}" if p.target_id else ""
        lines.append(f"- **{it.label}** ×{p.count}, {p.height_m:g} m, {p.surface.value}/{p.layout.value}{target} — "
                     f"`{it.asset_prompt}`")
    for e in plan.effects:
        lines.append(f"- effect {e.type.value} {e.tint_hex or ''} {', '.join(e.surfaces)}".rstrip())
    if not plan.items and not plan.effects:
        lines.append("- (nothing)")
    return lines


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--image", help="a camera-like photo sent with every prompt")
    ap.add_argument("--only", choices=["local", "claude"])
    args = ap.parse_args()
    image = base64.b64encode(Path(args.image).read_bytes()).decode() if args.image else None
    providers = [args.only] if args.only else ["local", "claude"]

    out = ROOT / "data" / "compare_planners"
    out.mkdir(parents=True, exist_ok=True)
    report = out / time.strftime("%Y%m%d-%H%M%S.md")
    md = [f"# Planner comparison {time.strftime('%Y-%m-%d %H:%M')}", "",
          f"Local model: `{os.environ.get('WHATIF_LOCAL_MODEL', 'default')}`, "
          f"Claude: `{os.environ.get('WHATIF_PLANNER_MODEL', 'default')}`, image: {'yes' if image else 'no'}", ""]
    totals = {p: [] for p in providers}
    for prompt in PROMPTS:
        md += [f"## {prompt}", ""]
        for provider in providers:
            req = PlanRequest(prompt=prompt, scene=ROOM, image_jpeg_b64=image, generate=False)
            t = time.perf_counter()
            try:
                plan = plan_scene(req, provider=provider)
                took = time.perf_counter() - t
                totals[provider].append(took)
                md += [f"**{provider}** ({took:.1f} s)", ""] + describe(plan) + [""]
                print(f"{provider:6s} {took:5.1f} s  {len(plan.items)} kinds  {prompt}")
            except PlannerError as e:
                md += [f"**{provider}**: failed — {e}", ""]
                print(f"{provider:6s} FAILED  {prompt}: {e}")
    md += ["## Times", ""]
    for p, ts in totals.items():
        if ts:
            md.append(f"- {p}: median {sorted(ts)[len(ts) // 2]:.1f} s, max {max(ts):.1f} s over {len(ts)} plans")
    report.write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"\nreport: {report}")


if __name__ == "__main__":
    main()
