"""Generated sounds: an ambience per scene and a few object sounds, from a text description.

Stable Audio Open (stabilityai/stable-audio-open-1.0, Stability AI community licence, gated on
Hugging Face: accept it once with the account whose token is on the server). Generated on the GPU
in a queue of its own, cached by description, made loopable (the end is crossfaded into the start)
and saved as 16-bit mono WAV (the headset plays them spatially). If the model can't be loaded
(licence not accepted, WHATIF_SOUNDS=0), sounds are skipped and the log says why.
"""

from __future__ import annotations

import hashlib
import logging
import threading
import time
import wave
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np

from .config import settings
from .schemas import SoundInfo

log = logging.getLogger("whatif.sounds")

_MODEL_ID = "stabilityai/stable-audio-open-1.0"


def sound_id(prompt: str, loop: bool) -> str:
    return hashlib.sha1(f"{prompt.strip().lower()}|{loop}|{settings.sound_seconds}".encode()).hexdigest()[:16]


class SoundStore:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.root.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._jobs: dict[str, SoundInfo] = {}
        self._pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="sound")
        self._pipe = None
        self._unavailable: str | None = (
            "switched off (WHATIF_SOUNDS=0)" if not settings.sounds
            else "no GPU generation in mock mode" if settings.image_provider == "mock" else None)

    def path(self, sid: str) -> Path:
        return self.root / f"{sid}.wav"

    def request(self, prompt: str, loop: bool) -> SoundInfo | None:
        """Start (or reuse) a sound; None if sounds are unavailable."""
        prompt = (prompt or "").strip()
        if not prompt or self._unavailable:
            return None
        sid = sound_id(prompt, loop)
        with self._lock:
            if sid in self._jobs:
                return self._jobs[sid]
            info = SoundInfo(id=sid, prompt=prompt, loop=loop, status="queued")
            if self.path(sid).exists():
                info.status, info.url = "done", f"/sounds/{sid}.wav"
            else:
                self._pool.submit(self._run, sid, prompt, loop)
            self._jobs[sid] = info
            return info

    def get(self, sid: str) -> SoundInfo | None:
        with self._lock:
            return self._jobs.get(sid)

    def _load(self):
        if self._pipe is None:
            import torch
            from diffusers import StableAudioPipeline

            t = time.perf_counter()
            pipe = StableAudioPipeline.from_pretrained(_MODEL_ID, torch_dtype=torch.float16)
            self._pipe = pipe.to("cuda" if torch.cuda.is_available() else "cpu")
            log.info("sound model loaded in %.1f s", time.perf_counter() - t)
        return self._pipe

    def _run(self, sid: str, prompt: str, loop: bool) -> None:
        try:
            import torch

            pipe = self._load()
            t = time.perf_counter()
            seconds = settings.sound_seconds if loop else min(4.0, settings.sound_seconds)
            gen = torch.Generator(pipe.device).manual_seed(int(sid[:8], 16) % 100000)
            audio = pipe(prompt, negative_prompt="low quality, distorted, music, speech",
                         num_inference_steps=settings.sound_steps, audio_end_in_s=seconds + (1.0 if loop else 0.0),
                         generator=gen).audios[0]
            rate = pipe.vae.sampling_rate
            mono = audio.float().cpu().numpy().mean(axis=0)
            if loop:
                mono = _loopable(mono, int(rate * 1.0))
            _write_wav(self.path(sid), mono, rate)
            with self._lock:
                self._jobs[sid].status, self._jobs[sid].url = "done", f"/sounds/{sid}.wav"
            log.info("sound  %s  %r  %.1f s in %.1f s", sid, prompt, len(mono) / rate, time.perf_counter() - t)
        except Exception as e:  # noqa: BLE001
            msg = str(e)
            if "gated" in msg.lower() or "401" in msg or "403" in msg:
                self._unavailable = "the Stable Audio Open licence isn't accepted on Hugging Face"
            elif isinstance(e, ImportError):
                self._unavailable = f"missing package ({e})"
            log.warning("sound failed for %r: %s%s", prompt, msg[:200],
                        f" - sounds off: {self._unavailable}" if self._unavailable else "")
            with self._lock:
                self._jobs[sid].status, self._jobs[sid].error = "failed", msg[:200]


def _loopable(x: np.ndarray, fade: int) -> np.ndarray:
    """Crossfade the last `fade` samples into the first ones, so the clip loops without a click."""
    if len(x) <= 2 * fade:
        return x
    head, tail = x[:fade], x[-fade:]
    ramp = np.linspace(0.0, 1.0, fade, dtype=np.float32)
    blended = tail * (1 - ramp) + head * ramp
    return np.concatenate([blended, x[fade:-fade]])


def _write_wav(path: Path, mono: np.ndarray, rate: int) -> None:
    peak = float(np.max(np.abs(mono))) or 1.0
    pcm = (np.clip(mono / peak * 0.9, -1, 1) * 32767).astype("<i2")
    tmp = path.with_suffix(".tmp")
    with wave.open(str(tmp), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())
    tmp.replace(path)


sounds = SoundStore(settings.data_dir / "sounds")
