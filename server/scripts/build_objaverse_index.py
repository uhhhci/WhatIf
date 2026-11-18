"""Builds the local Objaverse search index (once, ~10-20 minutes, ~1.5 GB download).

Downloads Objaverse's metadata (Allen Institute, ODC-By) and the Cap3D descriptions (ODC-By), keeps
the objects we may use (licence CC0 / CC-BY / CC-BY-SA, not age-restricted, GLB size and face
limits) and stores one CLIP text embedding per object:

    data/objaverse/embeddings.npy   float16, N x 512, normalised
    data/objaverse/entries.jsonl    uid, name, author, licence, path, faces, size, preview picture,
                                    Sketchfab likes / views / staff pick (quality signals)

Run on the GPU server while the What If server may keep running (CLIP needs ~1 GB):
    cd ~/whatif/server && .venv/bin/python scripts/build_objaverse_index.py
Then set WHATIF_OBJAVERSE=1 in .env and restart the server.
"""

from __future__ import annotations

import argparse
import csv
import gzip
import json
import sys
import time
from ast import literal_eval
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import numpy as np  # noqa: E402

from whatif.config import settings  # noqa: E402
from whatif.objaverse import HF_BASE, LICENCES, _UA, index_dir  # noqa: E402

CAP3D = "https://huggingface.co/datasets/tiange/Cap3D/resolve/main/Cap3D_automated_Objaverse_full.csv"
TREE = "https://huggingface.co/api/datasets/allenai/objaverse/tree/main/metadata"


def download(url: str, dst: Path) -> Path:
    import httpx

    if dst.exists() and dst.stat().st_size > 0:
        return dst
    dst.parent.mkdir(parents=True, exist_ok=True)
    tmp = dst.with_suffix(dst.suffix + ".part")
    with httpx.stream("GET", url, headers=_UA, timeout=120, follow_redirects=True) as r:
        r.raise_for_status()
        with open(tmp, "wb") as f:
            for chunk in r.iter_bytes(1 << 20):
                f.write(chunk)
    tmp.replace(dst)
    return dst


def field(value, default=None):
    """Objaverse stores nested fields as Python literals in strings."""
    if isinstance(value, str) and value[:1] in "[{":
        try:
            return literal_eval(value)
        except (ValueError, SyntaxError):
            return default
    return value if value is not None else default


def thumbnail(o: dict) -> str | None:
    images = (field(o.get("thumbnails"), {}) or {}).get("images", [])
    images = [i for i in images if i.get("url") and i.get("width")]
    if not images:
        return None
    return min(images, key=lambda i: abs(int(i["width"]) - 512))["url"]


def main() -> None:
    import httpx

    ap = argparse.ArgumentParser()
    ap.add_argument("--max-mb", type=float, default=60, help="largest GLB kept in the index")
    ap.add_argument("--max-faces", type=int, default=400_000, help="most faces kept in the index")
    ap.add_argument("--batch", type=int, default=512)
    args = ap.parse_args()

    root = index_dir()
    raw = root / "raw"
    t0 = time.perf_counter()

    print("object paths ...", flush=True)
    paths = json.load(gzip.open(download(HF_BASE + "object-paths.json.gz", raw / "object-paths.json.gz")))

    print("descriptions (Cap3D, ~250 MB) ...", flush=True)
    captions: dict[str, str] = {}
    with open(download(CAP3D, raw / "cap3d.csv"), encoding="utf-8", newline="") as f:
        for row in csv.reader(f):
            if len(row) >= 2 and row[0] in paths:
                captions[row[0]] = row[1].strip()

    files = [x["path"] for x in httpx.get(TREE, headers=_UA, timeout=60).json() if x["path"].endswith(".json.gz")]
    entries, texts = [], []
    for n, name in enumerate(sorted(files), 1):
        print(f"metadata {n}/{len(files)} ...", flush=True)
        meta = json.load(gzip.open(download(HF_BASE + name, raw / Path(name).name)))
        for uid, o in meta.items():
            licence = o.get("license")
            glb = (field(o.get("archives"), {}) or {}).get("glb") or {}
            faces = int(glb.get("faceCount") or o.get("faceCount") or 0)
            mb = float(glb.get("size") or 0) / 1e6
            if (licence not in LICENCES or uid not in paths or str(o.get("isAgeRestricted")) == "True"
                    or not faces or faces > args.max_faces or not mb or mb > args.max_mb):
                continue
            thumb = thumbnail(o)
            if not thumb:
                continue
            user = field(o.get("user"), {}) or {}
            tags = [t.get("name", "") for t in (field(o.get("tags"), []) or [])][:8]
            text = captions.get(uid) or f"{o.get('name', '')}. {', '.join(tags)}"
            entries.append({"uid": uid, "name": (o.get("name") or "")[:120],
                            "author": user.get("displayName") or user.get("username") or "unknown",
                            "license": licence, "path": paths[uid], "faces": faces, "mb": round(mb, 1),
                            "thumb": thumb, "text": text[:300],
                            "likes": int(o.get("likeCount") or 0), "views": int(o.get("viewCount") or 0),
                            "staff": bool(o.get("staffpickedAt"))})
            texts.append(text)
    print(f"{len(entries)} usable objects ({len(captions)} with a description)", flush=True)

    from whatif.generators import _clip_scorer

    scorer = _clip_scorer()
    if scorer is None:
        sys.exit("CLIP is not available")
    emb = np.zeros((len(texts), 512), dtype=np.float16)
    for i in range(0, len(texts), args.batch):
        emb[i:i + args.batch] = scorer.text_embeddings(texts[i:i + args.batch])
        if (i // args.batch) % 100 == 0:
            print(f"embeddings {i}/{len(texts)} ...", flush=True)
    root.mkdir(parents=True, exist_ok=True)
    np.save(root / "embeddings.npy", emb)
    with open(root / "entries.jsonl", "w", encoding="utf-8") as f:
        for e in entries:
            f.write(json.dumps(e, ensure_ascii=False) + "\n")
    print(f"done in {(time.perf_counter() - t0) / 60:.0f} min: {root}", flush=True)
    print(f"(the raw downloads in {raw} can be deleted)")


if __name__ == "__main__":
    main()
