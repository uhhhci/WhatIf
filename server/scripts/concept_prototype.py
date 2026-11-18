"""Offline test: can an edited photo of the room serve as the planner's concept?

For each test photo and scenario:
  1. edit:    an instruction image editor turns the room photo into the scenario, keeping the room
              and its furniture (AR can only add things, never hide real ones);
  2. read:    the planner's own vision model (Qwen3.5 in vLLM) compares original and edited picture
              and lists what was added - with a box, what it rests on, its rough height and layer -
              plus which real surfaces changed, the palette, the light and a short concept;
  3. report:  one self-contained HTML page (original | edited | edited with numbered boxes, the list,
              the concept, timings) to look at on the laptop.

Nothing here touches the running server. Runs in its own venv so the editor's newer diffusers can't
break the server's (see the commands below). The test photos are people-free Wikimedia Commons
pictures (credited in the report); --photos DIR uses your own instead. Nothing from the headset.

    # once: a separate venv (~10 GB of packages; the editor downloads ~55 GB of weights on first run)
    python3 -m venv ~/concept-venv
    ~/concept-venv/bin/pip install torch torchvision --index-url https://download.pytorch.org/whl/cu126
    ~/concept-venv/bin/pip install "diffusers>=0.36" "transformers>=4.57" accelerate pillow bitsandbytes

    # run (a GPU with ~45 GB free, see nvidia-smi - or add --nf4 for ~28 GB; vLLM must run on port 8017)
    cd ~/whatif/server && CUDA_VISIBLE_DEVICES=2 ~/concept-venv/bin/python scripts/concept_prototype.py

Result: data/concept_test/report.html (plus every picture and JSON next to it).
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

UA = {"User-Agent": "WhatIf/0.1 (https://github.com/uhhhci/WhatIf; AR research prototype)"}

# people-free rooms from Wikimedia Commons (three living rooms, three seminar rooms)
COMMONS = [
    "A standard living room in Accra.jpg",
    "Living room (13152023964).jpg",
    "At La Palma 2021 1854.jpg",
    "Admincon 2012 kloster seminarraum.jpg",
    "Ideenraum finales Foto.jpg",
    "LA Seminarraum.jpg",
]
SCENARIOS = ["a jungle", "the bottom of the sea", "a medieval tavern"]

EDIT_PROMPT = (
    "Turn this room into {s}. Keep the room itself exactly as it is: the same walls, floor, ceiling, "
    "windows and doors, and every piece of furniture where it is and recognisable. Do not remove or "
    "replace anything. Add {s} to it as real physical things at realistic size - standing on the floor, "
    "on the furniture, on the walls and hanging from the ceiling - so the room feels completely like {s}. "
    "Photorealistic, same camera position, same perspective, natural light."
)

LAYERS = ["ground_cover", "mid", "tall", "wall", "ceiling", "air"]
RESTS_ON = ["floor", "furniture_top", "wall", "ceiling", "hanging", "floating", "covers_real_object"]
SCHEMA = {
    "type": "object",
    "properties": {
        "concept": {"type": "string", "maxLength": 700, "description":
                    "3-5 sentences of art direction for building this scene in the real room: overall look, "
                    "density, how the layers from floor to ceiling are filled, focal point, what makes it read "
                    "as the scenario."},
        "palette": {"type": "array", "maxItems": 6, "items": {"type": "string", "maxLength": 7},
                    "description": "dominant colours of the added scenario as #rrggbb"},
        "light": {"type": "string", "maxLength": 200, "description": "light mood and colour"},
        "added_objects": {"type": "array", "maxItems": 24, "items": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "maxLength": 60},
                "description": {"type": "string", "maxLength": 200,
                                "description": "one sentence to generate this single object as it looks here"},
                "count": {"type": "integer", "minimum": 1, "maximum": 30,
                          "description": "how many of it are visible"},
                "bbox_2d": {"type": "array", "minItems": 4, "maxItems": 4, "items": {"type": "integer"},
                            "description": "x1,y1,x2,y2 around the most prominent one, in the EDITED picture, "
                                           "relative coordinates 0-1000"},
                "rests_on": {"type": "string", "enum": RESTS_ON},
                "real_anchor": {"type": "string", "maxLength": 80,
                                "description": "the real thing of the room it stands on, hangs from or is next to"},
                "approx_height_m": {"type": "number"},
                "layer": {"type": "string", "enum": LAYERS},
            },
            "required": ["name", "description", "count", "bbox_2d", "rests_on", "real_anchor",
                         "approx_height_m", "layer"],
        }},
        "changed_surfaces": {"type": "array", "maxItems": 8, "items": {
            "type": "object",
            "properties": {"surface": {"type": "string", "maxLength": 40},
                           "becomes": {"type": "string", "maxLength": 120}},
            "required": ["surface", "becomes"]}},
        "replaced_furniture": {"type": "array", "maxItems": 8, "items": {
            "type": "object",
            "properties": {"real_object": {"type": "string", "maxLength": 60},
                           "became": {"type": "string", "maxLength": 120}},
            "required": ["real_object", "became"]},
            "description": "real furniture the edit removed or turned into something else (AR cannot hide it)"},
    },
    "required": ["concept", "palette", "light", "added_objects", "changed_surfaces", "replaced_furniture"],
}
READ_PROMPT = (
    "Picture 1 is a real room. Picture 2 is the same room edited to look like {s}. "
    "List everything that was ADDED in picture 2 (not the room's own walls, windows and furniture), "
    "group identical things into one entry with a count, and give its box in picture 2. Note which real "
    "surfaces were re-skinned (floor, walls, ceiling, a sofa covered in moss...) and any real furniture "
    "that disappeared or turned into something else. Then write the concept: how would you rebuild "
    "picture 2's atmosphere in the real room with 3D objects?"
)


# ---------------------------------------------------------------- photos
def commons_photos(out: Path, width: int = 1600) -> list[dict]:
    q = urllib.parse.urlencode({"action": "query", "format": "json", "titles": "|".join("File:" + t for t in COMMONS),
                                "prop": "imageinfo", "iiprop": "url|extmetadata", "iiurlwidth": str(width),
                                "iiextmetadatafilter": "LicenseShortName|Artist"})
    req = urllib.request.Request("https://commons.wikimedia.org/w/api.php?" + q, headers=UA)
    pages = json.load(urllib.request.urlopen(req, timeout=30))["query"]["pages"].values()
    photos = []
    for p in sorted(pages, key=lambda p: p["title"]):
        if "imageinfo" not in p:
            print("not on Commons any more:", p["title"])
            continue
        ii = p["imageinfo"][0]
        meta = ii.get("extmetadata", {})
        author = re.sub("<[^>]+>", "", meta.get("Artist", {}).get("value", "unknown")).strip()
        name = p["title"][5:]
        path = out / ("photo_%d.jpg" % len(photos))
        if not path.exists():
            data = urllib.request.urlopen(urllib.request.Request(ii["thumburl"], headers=UA), timeout=60).read()
            path.write_bytes(data)
        photos.append({"path": path, "title": name, "credit": f"{author}, {meta.get('LicenseShortName', {}).get('value', '?')}",
                       "url": ii["descriptionurl"]})
    return photos


def own_photos(folder: Path) -> list[dict]:
    return [{"path": p, "title": p.name, "credit": "own photo", "url": ""}
            for p in sorted(folder.iterdir()) if p.suffix.lower() in (".jpg", ".jpeg", ".png")]


# ---------------------------------------------------------------- 1. edit
def load_editor(name: str, nf4: bool = False):
    import torch

    if name == "qwen":
        from diffusers import QwenImageEditPlusPipeline

        if nf4:
            # transformer in 4 bits, text encoder in 8 bits (bitsandbytes): ~21 GB instead of ~57 GB.
            # The first block's modulation stays in full precision: quantised, it leaves the picture
            # grainy (not fully denoised) - the first run looked exactly like that.
            from diffusers import BitsAndBytesConfig as DiffusersBnb
            from diffusers.quantizers import PipelineQuantizationConfig
            from transformers import BitsAndBytesConfig as TransformersBnb

            q = PipelineQuantizationConfig(quant_mapping={
                "transformer": DiffusersBnb(load_in_4bit=True, bnb_4bit_quant_type="nf4",
                                            bnb_4bit_compute_dtype=torch.bfloat16,
                                            llm_int8_skip_modules=["transformer_blocks.0.img_mod"]),
                "text_encoder": TransformersBnb(load_in_8bit=True),
            })
            pipe = QwenImageEditPlusPipeline.from_pretrained("Qwen/Qwen-Image-Edit-2509", torch_dtype=torch.bfloat16,
                                                             quantization_config=q)
            pipe.to("cuda")
        else:
            pipe = QwenImageEditPlusPipeline.from_pretrained("Qwen/Qwen-Image-Edit-2509", torch_dtype=torch.bfloat16)
            pipe.enable_model_cpu_offload()  # 20B transformer + 7B text encoder: one at a time on the GPU

        def run(img, prompt, steps, seed):
            return pipe(image=[img], prompt=prompt, negative_prompt=" ", true_cfg_scale=4.0,
                        num_inference_steps=steps, generator=torch.Generator("cpu").manual_seed(seed)).images[0]
    else:  # FLUX.1 Kontext dev (gated: accept the licence on Hugging Face first; non-commercial)
        from diffusers import FluxKontextPipeline

        pipe = FluxKontextPipeline.from_pretrained("black-forest-labs/FLUX.1-Kontext-dev", torch_dtype=torch.bfloat16)
        pipe.to("cuda")

        def run(img, prompt, steps, seed):
            return pipe(image=img, prompt=prompt, guidance_scale=2.5, num_inference_steps=steps,
                        generator=torch.Generator("cpu").manual_seed(seed)).images[0]
    return run


# ---------------------------------------------------------------- 2. read
def b64(img: Image.Image, size: int = 896) -> str:
    img = img.convert("RGB").copy()
    img.thumbnail((size, size))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=88)
    return base64.b64encode(buf.getvalue()).decode()


def _plain(schema):
    """The schema without number and length limits (for a vLLM that rejects them)."""
    if isinstance(schema, dict):
        return {k: _plain(v) for k, v in schema.items()
                if k not in ("minimum", "maximum", "minItems", "maxItems", "maxLength")}
    if isinstance(schema, list):
        return [_plain(v) for v in schema]
    return schema


def side_by_side(original: Image.Image, edited: Image.Image, height: int = 640) -> tuple[Image.Image, int, int]:
    a = original.convert("RGB").resize((round(original.width * height / original.height), height))
    b = edited.convert("RGB").resize((round(edited.width * height / edited.height), height))
    both = Image.new("RGB", (a.width + b.width, height), "white")
    both.paste(a, (0, 0))
    both.paste(b, (a.width, 0))
    return both, a.width, b.width


SIDE_NOTE = (" (Both are in ONE picture here: the real room on the left, the edited room on the right. "
             "Give every box in coordinates 0-1000 of the WHOLE picture; the boxes lie in the right half.)")


def read_scene(url: str, model: str, original: Image.Image, edited: Image.Image, scenario: str) -> dict:
    """Two pictures in one request; if vLLM refuses that (it may allow one picture per request), both
    side by side in one picture; then without the schema's limits. vLLM's answers end up in the error."""
    errors = []
    for mode, schema in (("two", SCHEMA), ("side", SCHEMA), ("side", _plain(SCHEMA))):
        if mode == "two":
            images = [b64(original), b64(edited)]
            text = READ_PROMPT.format(s=scenario)
        else:
            both, wl, wr = side_by_side(original, edited)
            images = [b64(both, 1400)]
            text = READ_PROMPT.format(s=scenario) + SIDE_NOTE
        body = {
            "model": model,
            "messages": [
                {"role": "system", "content": "You analyse pictures for an AR app that adds 3D objects to real rooms. "
                                              "Answer with one JSON object following this schema:\n" + json.dumps(schema)},
                {"role": "user", "content": [{"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + i}}
                                             for i in images] + [{"type": "text", "text": text}]},
            ],
            "temperature": 0.2,
            "max_tokens": 4000,
            "response_format": {"type": "json_schema",
                                "json_schema": {"name": "scene", "schema": schema, "strict": True}},
            "chat_template_kwargs": {"enable_thinking": False},
        }
        req = urllib.request.Request(url.rstrip("/") + "/chat/completions", data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
        try:
            answer = json.load(urllib.request.urlopen(req, timeout=300))
        except urllib.error.HTTPError as e:
            errors.append(f"[{mode}] {e.code}: {e.read().decode(errors='replace')[:300]}")
            print("  reading:", errors[-1])
            continue
        scene = json.loads(answer["choices"][0]["message"]["content"])
        if mode == "side":  # boxes from the whole picture to the edited (right) part
            def fx(x):
                return max(0, min(1000, round(((x / 1000) * (wl + wr) - wl) / wr * 1000)))
            for o in scene.get("added_objects", []):
                x1, y1, x2, y2 = (list(o.get("bbox_2d", [])) + [0, 0, 0, 0])[:4]
                o["bbox_2d"] = [fx(x1), y1, fx(x2), y2]
        scene["_reading"] = mode
        return scene
    raise RuntimeError(" | ".join(errors))


def vlm_model(url: str) -> str:
    return json.load(urllib.request.urlopen(url.rstrip("/") + "/models", timeout=10))["data"][0]["id"]


# ---------------------------------------------------------------- 3. report
COLOURS = ["#e6194b", "#3cb44b", "#ffe119", "#4363d8", "#f58231", "#911eb4", "#46f0f0", "#f032e6",
           "#bcf60c", "#fabebe", "#008080", "#e6beff"]


def draw_boxes(img: Image.Image, objects: list[dict]) -> Image.Image:
    out = img.convert("RGB").copy()
    d = ImageDraw.Draw(out)
    w, h = out.size
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", max(14, w // 50))
    except OSError:
        font = ImageFont.load_default()
    lw = max(2, w // 300)
    for i, o in enumerate(objects):
        x1, y1, x2, y2 = (o["bbox_2d"] + [0, 0, 0, 0])[:4]
        box = [x1 * w / 1000, y1 * h / 1000, x2 * w / 1000, y2 * h / 1000]
        c = COLOURS[i % len(COLOURS)]
        d.rectangle(box, outline=c, width=lw)
        d.text((box[0] + lw + 2, box[1] + lw), str(i + 1), fill=c, font=font, stroke_width=2, stroke_fill="black")
    return out


def img_tag(img: Image.Image, size: int = 900) -> str:
    return f'<img src="data:image/jpeg;base64,{b64(img, size)}">'


def esc(s) -> str:
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def case_html(photo: dict, scenario: str, original, edited, scene: dict | None, t_edit: float, t_read: float,
              error: str | None) -> str:
    h = [f'<section><h2>{esc(photo["title"])} &rarr; {esc(scenario)}</h2>',
         f'<p class="meta">edit {t_edit:.0f} s &middot; reading {t_read:.0f} s</p>']
    if edited is None:
        return "".join(h) + f'<p class="err">{esc(error)}</p></section>'
    objects = (scene or {}).get("added_objects", [])
    h.append('<div class="row">' + img_tag(original) + img_tag(edited) + img_tag(draw_boxes(edited, objects)) + "</div>")
    if scene is None:
        return "".join(h) + f'<p class="err">reading failed: {esc(error)}</p></section>'
    sw = "".join(f'<span class="sw" style="background:{esc(c)}"></span>' for c in scene.get("palette", []))
    h.append(f'<p><b>Concept.</b> {esc(scene["concept"])}</p><p><b>Light.</b> {esc(scene["light"])} {sw}</p>')
    h.append("<table><tr><th>#</th><th>object</th><th>count</th><th>rests on</th><th>real anchor</th>"
             "<th>height</th><th>layer</th><th>description</th></tr>")
    for i, o in enumerate(objects):
        c = COLOURS[i % len(COLOURS)]
        h.append(f'<tr><td style="color:{c}"><b>{i + 1}</b></td><td>{esc(o["name"])}</td><td>{o["count"]}</td>'
                 f'<td>{esc(o["rests_on"])}</td><td>{esc(o["real_anchor"])}</td><td>{o["approx_height_m"]:.2f} m</td>'
                 f'<td>{esc(o["layer"])}</td><td>{esc(o["description"])}</td></tr>')
    h.append("</table>")
    if scene.get("changed_surfaces"):
        h.append("<p><b>Re-skinned surfaces:</b> " + "; ".join(
            f'{esc(s["surface"])} &rarr; {esc(s["becomes"])}' for s in scene["changed_surfaces"]) + "</p>")
    if scene.get("replaced_furniture"):
        h.append('<p class="err"><b>Real furniture the edit removed or changed (AR cannot do this):</b> ' + "; ".join(
            f'{esc(s["real_object"])} &rarr; {esc(s["became"])}' for s in scene["replaced_furniture"]) + "</p>")
    return "".join(h) + "</section>"


PAGE = """<!doctype html><html><head><meta charset="utf-8"><title>Concept test</title><style>
body{font:14px/1.45 system-ui,sans-serif;margin:16px;background:#fafafa;color:#222}
section{background:#fff;border:1px solid #ddd;border-radius:8px;padding:12px 16px;margin:0 0 20px}
.row{display:grid;grid-template-columns:repeat(3,1fr);gap:6px}.row img{width:100%;border-radius:4px}
table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #eee;padding:3px 6px;text-align:left;vertical-align:top}
.meta{color:#777;margin:0 0 6px}.err{color:#b00}.sw{display:inline-block;width:18px;height:18px;border-radius:3px;margin-left:4px;vertical-align:middle;border:1px solid #0003}
</style></head><body><h1>Edited photo as the planner's concept</h1><p>{intro}</p>{body}<h3>Photos</h3><ul>{credits}</ul></body></html>"""


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--editor", choices=["qwen", "kontext"], default="qwen")
    ap.add_argument("--nf4", action="store_true", help="qwen in 4/8 bits (~21 GB, needs bitsandbytes): for a GPU "
                                                        "with less than ~45 GB free")
    ap.add_argument("--steps", type=int, default=0, help="denoising steps (default: 40 qwen, 28 kontext)")
    ap.add_argument("--scenario", action="append", help="instead of the three defaults; repeatable")
    ap.add_argument("--photos", type=Path, help="a folder with your own room photos instead of the Commons ones")
    ap.add_argument("--limit", type=int, default=0, help="only the first N photos")
    ap.add_argument("--reuse", action="store_true",
                    help="keep the edited pictures of an earlier run and only do the reading again")
    ap.add_argument("--vlm-url", default="http://127.0.0.1:8017/v1")
    ap.add_argument("--out", type=Path, default=Path(__file__).resolve().parent.parent / "data" / "concept_test")
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    scenarios = a.scenario or SCENARIOS
    steps = a.steps or (40 if a.editor == "qwen" else 28)

    photos = own_photos(a.photos) if a.photos else commons_photos(a.out)
    if a.limit:
        photos = photos[: a.limit]
    model = vlm_model(a.vlm_url)
    print(f"{len(photos)} photos x {len(scenarios)} scenarios, editor {a.editor} ({steps} steps), reading with {model}")

    edit = None  # loaded when the first picture needs editing (never with --reuse and old pictures)

    sections, times = [], []
    for pi, photo in enumerate(photos):
        original = Image.open(photo["path"]).convert("RGB")
        original.thumbnail((1024, 1024))
        for si, scenario in enumerate(scenarios):
            stem = a.out / f"p{pi}_s{si}"
            edited = scene = error = None
            t_edit = t_read = 0.0
            try:
                t = time.perf_counter()
                if a.reuse and stem.with_suffix(".edited.jpg").exists():  # only redo the reading
                    edited = Image.open(stem.with_suffix(".edited.jpg")).convert("RGB")
                else:
                    if edit is None:
                        edit = load_editor(a.editor, a.nf4)
                        print(f"editor loaded in {time.perf_counter() - t:.0f} s")
                        t = time.perf_counter()
                    edited = edit(original, EDIT_PROMPT.format(s=scenario), steps, a.seed)
                    edited.save(stem.with_suffix(".edited.jpg"), quality=92)
                t_edit = time.perf_counter() - t
                t = time.perf_counter()
                scene = read_scene(a.vlm_url, model, original, edited, scenario)
                t_read = time.perf_counter() - t
                stem.with_suffix(".json").write_text(json.dumps(scene, indent=2, ensure_ascii=False), encoding="utf-8")
                times.append((t_edit, t_read))
            except Exception as e:  # noqa: BLE001 - keep going, show it in the report
                error = f"{type(e).__name__}: {e}"
                print("  failed:", error)
            n = len((scene or {}).get("added_objects", []))
            print(f"{photo['title'][:40]:40s} -> {scenario:22s} edit {t_edit:5.1f} s, read {t_read:5.1f} s, {n} objects")
            sections.append(case_html(photo, scenario, original, edited, scene, t_edit, t_read, error))
            # rewritten after every case: the report can be looked at while the run goes on
            avg = (f"Average: edit {sum(x for x, _ in times) / len(times):.0f} s, reading "
                   f"{sum(y for _, y in times) / len(times):.0f} s." if times else "")
            intro = (f"Editor: {a.editor}{' 4-bit' if a.nf4 else ''} ({steps} steps, seed {a.seed}). Reading: {esc(model)}. {avg} "
                     "Left: real room. Middle: edited. Right: what the reading step found (numbers = table rows).")
            credits = "".join(f'<li>{esc(p["title"])} &mdash; {esc(p["credit"])} '
                              f'{"<a href=" + chr(34) + esc(p["url"]) + chr(34) + ">source</a>" if p["url"] else ""}</li>'
                              for p in photos)
            (a.out / "report.html").write_text(PAGE.replace("{intro}", intro).replace("{body}", "".join(sections))
                                               .replace("{credits}", credits), encoding="utf-8")
    print("report:", a.out / "report.html")


if __name__ == "__main__":
    main()
