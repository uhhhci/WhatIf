"""TRELLIS.2 as a small local worker for the What If server.

TRELLIS.2 needs its own CUDA build (its own Python environment, ~/trellis-venv), so it runs as a
separate process and our server asks it for 3D models over HTTP on localhost. Only the standard
library is used for the HTTP part, so nothing has to be installed into the TRELLIS environment.

Run on the GPU server (from the TRELLIS.2 checkout, in its environment):
    cd ~/TRELLIS.2 && source ~/trellis-venv/bin/activate
    CUDA_VISIBLE_DEVICES=0 python ~/whatif/server/scripts/trellis_worker.py

API (localhost only):
    GET  /health                       -> {"ok": true, "model": ...}
    POST /mesh?faces=40000&texture=1024&type=512   body: PNG cut-out (RGBA)  -> GLB bytes
"""

from __future__ import annotations

import argparse
import io
import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")


def _token_from_server_env() -> None:
    """The gated DINOv3 download needs the Hugging Face token: take it from the What If server's .env
    (next to this script's folder) unless it is already set or stored by `hf auth login`."""
    if os.environ.get("HF_TOKEN") or os.environ.get("HUGGING_FACE_HUB_TOKEN"):
        return
    env = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), ".env")
    try:
        with open(env, encoding="utf-8") as f:
            for line in f:
                key, _, value = line.strip().partition("=")
                if key in ("HF_TOKEN", "HUGGING_FACE_HUB_TOKEN") and value:
                    os.environ["HF_TOKEN"] = value.strip().strip('"').strip("'")
    except OSError:
        pass


_token_from_server_env()

# TRELLIS.2's code isn't an installed package: it lives in its checkout (~/TRELLIS.2 or the current
# folder). Python only looks next to this script, so add the checkout to the search path.
import sys  # noqa: E402

for _dir in (os.environ.get("TRELLIS_DIR"), os.getcwd(), os.path.expanduser("~/TRELLIS.2")):
    if _dir and os.path.isdir(os.path.join(_dir, "trellis2")) and _dir not in sys.path:
        sys.path.insert(0, _dir)
        break

from PIL import Image  # noqa: E402

_pipeline = None
_lock = threading.Lock()  # one model at a time on the GPU


def load(model: str):
    global _pipeline
    from trellis2.pipelines import Trellis2ImageTo3DPipeline
    from trellis2.pipelines import rembg

    # TRELLIS.2 loads briaai/RMBG-2.0 (gated, non-commercial) for background removal. We always send
    # finished cut-outs, so it is never used: load the original BiRefNet (MIT, not gated) instead,
    # the same model our server uses.
    original_init = rembg.BiRefNet.__init__

    def init(self, model_name: str = "ZhengPeng7/BiRefNet"):
        if model_name == "briaai/RMBG-2.0":
            model_name = os.environ.get("TRELLIS_REMBG_MODEL", "ZhengPeng7/BiRefNet")
        original_init(self, model_name)

    rembg.BiRefNet.__init__ = init

    t = time.perf_counter()
    _pipeline = Trellis2ImageTo3DPipeline.from_pretrained(model)
    _pipeline.cuda()
    print(f"TRELLIS.2 loaded in {time.perf_counter() - t:.0f} s", flush=True)


def make_glb(png: bytes, faces: int, texture: int, pipeline_type: str, seed: int) -> bytes:
    import o_voxel
    import torch

    image = Image.open(io.BytesIO(png)).convert("RGBA")  # our cut-outs: the alpha channel is used as is
    with _lock, torch.no_grad():
        t = time.perf_counter()
        mesh = _pipeline.run(image, seed=seed, pipeline_type=pipeline_type)[0]
        mesh.simplify(16777216)  # nvdiffrast limit
        glb = o_voxel.postprocess.to_glb(
            vertices=mesh.vertices, faces=mesh.faces, attr_volume=mesh.attrs, coords=mesh.coords,
            attr_layout=mesh.layout, voxel_size=mesh.voxel_size,
            aabb=[[-0.5, -0.5, -0.5], [0.5, 0.5, 0.5]],
            decimation_target=faces, texture_size=texture,
            remesh=True, remesh_band=1, remesh_project=0, verbose=False,
        )
        buf = io.BytesIO()
        glb.export(buf, file_type="glb")  # PNG textures (the headset's glTF loader has no WebP)
        torch.cuda.empty_cache()
    print(f"mesh: {pipeline_type}, {faces} faces, {texture} px texture in {time.perf_counter() - t:.1f} s",
          flush=True)
    return buf.getvalue()


class Handler(BaseHTTPRequestHandler):
    model_name = ""

    def _send(self, code: int, body: bytes, kind: str) -> None:
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802
        if urlparse(self.path).path == "/health":
            self._send(200, json.dumps({"ok": _pipeline is not None, "model": self.model_name}).encode(),
                       "application/json")
        else:
            self._send(404, b"not found", "text/plain")

    def do_POST(self):  # noqa: N802
        url = urlparse(self.path)
        if url.path != "/mesh":
            self._send(404, b"not found", "text/plain")
            return
        q = parse_qs(url.query)
        try:
            png = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            glb = make_glb(png, int(q.get("faces", ["40000"])[0]), int(q.get("texture", ["1024"])[0]),
                           q.get("type", ["512"])[0], int(q.get("seed", ["42"])[0]))
            self._send(200, glb, "model/gltf-binary")
        except Exception as e:  # noqa: BLE001 - report to the What If server, keep running
            print(f"mesh failed: {e!r}", flush=True)
            self._send(500, repr(e).encode()[:2000], "text/plain")

    def log_message(self, *args):  # quiet: one line per model is printed above
        pass


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8019)
    ap.add_argument("--model", default="microsoft/TRELLIS.2-4B")
    args = ap.parse_args()
    load(args.model)
    Handler.model_name = args.model
    server = HTTPServer(("127.0.0.1", args.port), Handler)  # localhost only
    print(f"TRELLIS.2 worker on http://127.0.0.1:{args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
