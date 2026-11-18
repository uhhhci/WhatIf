"""Adds Sketchfab's quality signals to an existing Objaverse index, without rebuilding it:
likes, views and the staff-pick mark of every object, read from Objaverse's metadata files that
build_objaverse_index.py downloaded (data/objaverse/raw/*.json.gz; missing ones are downloaded).
Only entries.jsonl changes (same order, so the embeddings stay valid). Takes a few minutes.
    cd ~/whatif/server && .venv/bin/python scripts/add_objaverse_quality.py
Then restart the server.
"""
from __future__ import annotations

import gzip
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from scripts.build_objaverse_index import TREE, download  # noqa: E402
from whatif.objaverse import HF_BASE, _UA, index_dir  # noqa: E402


def main() -> None:
    import httpx

    root = index_dir()
    path = root / "entries.jsonl"
    with open(path, encoding="utf-8") as f:
        entries = [json.loads(line) for line in f]
    by_uid = {e["uid"]: e for e in entries}
    print(f"{len(entries)} objects in the index", flush=True)

    files = [x["path"] for x in httpx.get(TREE, headers=_UA, timeout=60).json() if x["path"].endswith(".json.gz")]
    found = staff = 0
    for n, name in enumerate(sorted(files), 1):
        print(f"metadata {n}/{len(files)} ...", flush=True)
        meta = json.load(gzip.open(download(HF_BASE + name, root / "raw" / Path(name).name)))
        for uid, o in meta.items():
            e = by_uid.get(uid)
            if e is None:
                continue
            e["likes"] = int(o.get("likeCount") or 0)
            e["views"] = int(o.get("viewCount") or 0)
            e["staff"] = bool(o.get("staffpickedAt"))
            found += 1
            staff += e["staff"]
    tmp = path.with_suffix(".jsonl.part")
    with open(tmp, "w", encoding="utf-8") as f:
        for e in entries:
            f.write(json.dumps(e, ensure_ascii=False) + "\n")
    tmp.replace(path)
    print(f"done: quality for {found} of {len(entries)} objects, {staff} staff picks", flush=True)


if __name__ == "__main__":
    main()
