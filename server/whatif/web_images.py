"""Real pictures from the web as candidates next to the generated ones.

For a specific real thing whose look matters (a famous building, a classic car model, a historical
figure), the planner writes a short search query. Free, openly licensed pictures are fetched and
compete with the generated images in the normal selection (background removal, cut-off check,
CLIP match) - a web picture wins only when it really fits better.

Rules:
- Only public-domain / CC0 pictures (no attribution or share-alike obligations to track).
- People: only historical figures. The query must resolve on Wikidata to a human who died at least
  70 years ago; then only that person's own Wikidata picture is used (its licence checked too).
  Living people and private individuals are never searched: a "person" query that doesn't resolve
  that way returns nothing, and a "thing" query that resolves to a human is dropped as well.
- Nothing about the user is sent: only the planner's short object query. Downloads stay in memory.
"""

from __future__ import annotations

import io
import logging
import re
from datetime import date

from PIL import Image

from .config import settings

log = logging.getLogger("whatif.web")

_UA = {"User-Agent": "WhatIf/0.1 (https://github.com/uhhhci/WhatIf; AR research prototype) python-httpx"}
_WIKIDATA = "https://www.wikidata.org/w/api.php"
_COMMONS = "https://commons.wikimedia.org/w/api.php"
_OPENVERSE = "https://api.openverse.org/v1/images/"
_MAX_BYTES = 8_000_000
_PD = re.compile(r"^(public domain|pd\b|pd-|cc0|cc-zero|no restrictions)", re.I)


def _get(url: str, params: dict | None = None):
    import httpx

    r = httpx.get(url, params=params, headers=_UA, timeout=settings.web_timeout_s, follow_redirects=True)
    r.raise_for_status()
    return r


def _human(query: str) -> dict | None:
    """The Wikidata human a query names, as {died: year | None, image: filename | None}, else None."""
    found = _get(_WIKIDATA, {"action": "wbsearchentities", "search": query, "language": "en", "type": "item",
                             "limit": 3, "format": "json"}).json().get("search", [])
    ids = [f["id"] for f in found]
    if not ids:
        return None
    entities = _get(_WIKIDATA, {"action": "wbgetentities", "ids": "|".join(ids), "props": "claims",
                                "format": "json"}).json().get("entities", {})
    for qid in ids:  # search order: the best match first
        claims = entities.get(qid, {}).get("claims", {})
        kinds = [c.get("mainsnak", {}).get("datavalue", {}).get("value", {}).get("id") for c in claims.get("P31", [])]
        if "Q5" not in kinds:
            continue
        died = None
        for c in claims.get("P570", []):
            t = c.get("mainsnak", {}).get("datavalue", {}).get("value", {}).get("time", "")
            m = re.match(r"^([+-]\d+)-", t)
            if m:
                died = int(m.group(1))
        image = None
        for c in claims.get("P18", []):
            image = c.get("mainsnak", {}).get("datavalue", {}).get("value")
        return {"died": died, "image": image}
    return None


def historical(person: dict | None) -> bool:
    """A human who died at least 70 years ago (public domain era); living or unknown: no."""
    return bool(person) and person.get("died") is not None and person["died"] <= date.today().year - 70


def _commons_files(params: dict) -> list[str]:
    """Thumbnail URLs of Commons files whose licence is public domain / CC0."""
    data = _get(_COMMONS, {"action": "query", "prop": "imageinfo", "iiprop": "url|extmetadata|mime",
                           "iiurlwidth": 1024, "format": "json", **params}).json()
    urls = []
    for page in data.get("query", {}).get("pages", {}).values():
        for info in page.get("imageinfo", []):
            licence = info.get("extmetadata", {}).get("LicenseShortName", {}).get("value", "")
            if info.get("mime") in ("image/jpeg", "image/png") and _PD.match(licence.strip()):
                urls.append(info.get("thumburl") or info.get("url"))
    return [u for u in urls if u]


def _openverse(query: str, n: int) -> list[str]:
    data = _get(_OPENVERSE, {"q": query, "license": "cc0,pdm", "page_size": n, "mature": "false"}).json()
    return [r.get("url") for r in data.get("results", []) if r.get("url")]


def _download(url: str) -> Image.Image | None:
    try:
        r = _get(url)
        if len(r.content) > _MAX_BYTES:
            return None
        img = Image.open(io.BytesIO(r.content))
        img.load()
        if min(img.size) < 256:
            return None
        img.thumbnail((1024, 1024))
        return img.convert("RGB")
    except Exception as e:  # noqa: BLE001 - one bad picture doesn't matter
        log.info("web picture skipped (%s)", e)
        return None


def find(query: str, kind: str = "thing") -> list[Image.Image]:
    """Openly licensed pictures for the planner's query ("person": historical figures only)."""
    query = (query or "").strip()[:80]
    if not query or not settings.web_images or settings.image_provider == "mock":
        return []
    n = max(1, settings.web_candidates)
    try:
        person = _human(query)
        if kind == "person" or person is not None:
            if not historical(person):
                log.info("web: %r is not a historical figure - no search", query)
                return []
            urls = _commons_files({"titles": "File:" + person["image"]}) if person.get("image") else []
        else:
            urls = _commons_files({"generator": "search", "gsrsearch": query, "gsrnamespace": 6, "gsrlimit": n * 2})
            if len(urls) < n:
                urls += _openverse(query, n)
    except Exception as e:  # noqa: BLE001 - the generated pictures still work
        log.warning("web search for %r failed: %s", query, e)
        return []
    images = [img for img in (_download(u) for u in urls[: n + 2]) if img is not None][:n]
    log.info("web: %d picture(s) for %r", len(images), query)
    return images
