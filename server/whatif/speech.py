"""Speech-to-text for voice prompts, fully local (faster-whisper).

Runs on the GPU when one is available (a sentence in a fraction of a second, ~1 GB for "small"),
else on the CPU (~2 s). A GPU that can't run it (missing CUDA libraries) falls back to the CPU at
start-up, so speech never stops working. Audio is only held in memory for one transcription.
"""

from __future__ import annotations

import io
import logging
import os
import sys
import threading
import time
import wave

import numpy as np

from .config import settings

log = logging.getLogger("whatif.speech")

_model = None
_lock = threading.Lock()


def _cuda_libraries() -> None:
    """faster-whisper (CTranslate2) needs cuBLAS 12 and cuDNN 9. PyTorch's CUDA wheels already
    bring them, just not on the library search path: make them findable."""
    try:
        import torch
    except ImportError:
        return
    if sys.platform == "win32":
        lib = os.path.join(os.path.dirname(torch.__file__), "lib")
        if os.path.isdir(lib):
            os.add_dll_directory(lib)
        return
    # Linux: the pip packages nvidia-cublas-cu12 / nvidia-cudnn-cu12 next to torch
    import ctypes
    import glob

    site = os.path.dirname(os.path.dirname(torch.__file__))
    for pattern in ("nvidia/cublas/lib/libcublasLt.so.12", "nvidia/cublas/lib/libcublas.so.12",
                    "nvidia/cudnn/lib/libcudnn*.so.9"):
        for path in sorted(glob.glob(os.path.join(site, pattern))):
            try:
                ctypes.CDLL(path, mode=ctypes.RTLD_GLOBAL)
            except OSError:
                pass


def _device() -> str:
    want = settings.whisper_device
    if want != "auto":
        return want
    try:
        import ctranslate2

        return "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
    except Exception:  # noqa: BLE001
        return "cpu"


def _load(device: str):
    from faster_whisper import WhisperModel

    if device == "cuda":
        _cuda_libraries()
        model = WhisperModel(model_name("cuda"), device="cuda", compute_type="float16")
        # a missing CUDA library only shows when the model runs: try one second of silence
        list(model.transcribe(np.zeros(16000, dtype=np.float32), language="en", beam_size=1)[0])
        return model
    return WhisperModel(model_name("cpu"), device="cpu", compute_type="int8")


def model_name(device: str) -> str:
    """WHATIF_WHISPER_MODEL, with "auto" = large-v3-turbo on the GPU and small on the CPU."""
    if settings.whisper_model != "auto":
        return settings.whisper_model
    return "large-v3-turbo" if device == "cuda" else "small"


def _get_model():
    global _model
    with _lock:
        if _model is None:
            t = time.perf_counter()
            device = _device()
            try:
                _model = _load(device)
            except Exception as e:  # noqa: BLE001 - the CPU always works
                if device == "cpu":
                    raise
                log.warning("whisper on the GPU failed (%s), using the CPU", e)
                device = "cpu"
                _model = _load(device)
            log.info("whisper %s loaded on the %s in %.1f s", model_name(device), "GPU" if device == "cuda" else "CPU",
                     time.perf_counter() - t)
        return _model


def ready() -> bool:
    """For /health: the speech model is loaded."""
    return _model is not None


def warm_up() -> None:
    try:
        _get_model()
    except Exception:  # noqa: BLE001 - surfaces again on the first request
        log.exception("whisper warm-up failed")


def read_wav(data: bytes) -> np.ndarray:
    """16-bit PCM WAV (any rate, mono or stereo) -> float32 mono at 16 kHz."""
    try:
        with wave.open(io.BytesIO(data), "rb") as w:
            if w.getsampwidth() != 2:
                raise ValueError("expected 16-bit PCM WAV")
            rate, channels = w.getframerate(), w.getnchannels()
            pcm = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
    except (wave.Error, EOFError) as e:
        raise ValueError(f"not a valid WAV file: {e}") from e
    if channels > 1:
        pcm = pcm.reshape(-1, channels).mean(axis=1)
    if rate != 16000:
        n = int(round(len(pcm) * 16000 / rate))
        pcm = np.interp(np.linspace(0, len(pcm) - 1, n), np.arange(len(pcm)), pcm).astype(np.float32)
    return pcm


def allowed_languages() -> list[str]:
    return [code.strip() for code in settings.whisper_languages.split(",") if code.strip()]


def pick_language(model, audio: np.ndarray, hint: str | None = None, override: float = 0.8) -> str | None:
    """Language of the sentence, among the allowed ones (e.g. English or German).

    `hint` is the language of the keyword the headset heard ("What if" / "Was wäre wenn"). It is
    used unless Whisper is very sure (> `override`) the sentence is in another allowed language:
    the offline keyword spotters can confuse the two keywords.
    """
    allowed = allowed_languages()
    if hint not in allowed:
        hint = None
    if len(allowed) <= 1:
        return allowed[0] if allowed else hint
    try:
        _, _, probs = model.detect_language(audio)
    except Exception:  # noqa: BLE001 - older faster-whisper: trust the hint or detect freely
        return hint
    scores = {code: p for code, p in probs if code in allowed}
    if not scores:
        return hint or allowed[0]
    best = max(scores, key=scores.get)
    if hint and best != hint and scores[best] <= override:
        return hint
    return best


# Hint phrase per language: biases spelling towards how prompts start.
_INITIAL_PROMPTS = {"en": "What if this place", "de": "Was wäre, wenn dieser Ort"}


def transcribe(wav_bytes: bytes, language: str | None = None, context: str | None = None) -> dict:
    """`language`: what the headset's keyword spotter heard ("en" for "What if", "de" for
    "Was wäre wenn"). Without it, the language is detected among the allowed ones."""
    audio = read_wav(wav_bytes)
    t = time.perf_counter()
    model = _get_model()
    language = pick_language(model, audio, hint=language)
    segments, info = model.transcribe(
        audio,
        language=language,
        beam_size=5,
        vad_filter=False,  # the headset already cut the utterance at the pause
        # no initial prompt: with "What if this place" as context, Whisper (turbo especially) left out
        # the same words at the start of the audio ("... would be a jungle")
        # words of the current scene (its scenario and objects: "Goethe", "Boulderhalle"), so names
        # that are already there are expected instead of heard as similar common words
        hotwords=((context or "").strip()[:150] or None) if settings.whisper_hints else None,
        condition_on_previous_text=False,
    )
    segments = list(segments)
    text = " ".join(s.text.strip() for s in segments).strip()
    took = time.perf_counter() - t
    speech = is_speech(text, segments)
    if speech and context and settings.whisper_hints and _only_hints(text, context) and len(audio) < 1.5 * 16000:
        speech = False  # a hint word "heard" in a short noise, not said
    seconds = len(audio) / 16000
    del audio, segments  # the recording is not kept once it is transcribed
    shown = repr(text) if settings.log_user_content else f"{len(text.split())} words"
    log.info("transcribed %.1f s of audio in %.1f s: %s%s", seconds, took, shown, "" if speech else " (ignored)")
    return {"text": text, "language": info.language, "audio_seconds": seconds, "seconds": took,
            "speech": speech}


# What Whisper typically "hears" in silence or noise (from its subtitle training data).
_PHANTOMS = {
    "thank you", "thank you.", "thanks for watching", "thanks for watching!", "thank you for watching",
    "you", "bye", "bye.", "okay", "okay.", "so", "hmm", "uh", "um",
    "untertitel im auftrag des zdf, 2017", "untertitel im auftrag des zdf", "untertitel der amara.org-community",
    "vielen dank", "vielen dank.", "danke", "danke.", "tschüss", "ja", "ja.",
}


def _only_hints(text: str, context: str) -> bool:
    """True if every word of the transcript is one of the hint words."""
    import re

    words = re.findall(r"\w+", text.lower())
    hints = set(re.findall(r"\w+", context.lower()))
    return bool(words) and all(w in hints for w in words)


def is_speech(text: str, segments) -> bool:
    """False for silence, noise and Whisper's phantom phrases. With continuous listening (no
    keyword) every cough reaches the server; only real sentences may become edits."""
    t = text.strip().lower()
    if len(t) < 3 or t in _PHANTOMS or "amara.org" in t or "untertitel" in t:
        return False
    if not segments:
        return False
    words = t.split()
    if len(words) >= 4 and max(words.count(w) for w in set(words)) > len(words) / 2:
        return False  # "is a a a a a a" - a hallucinated loop on noise
    if any(getattr(s, "compression_ratio", 0.0) > 2.4 for s in segments):
        return False  # very repetitive text: Whisper's standard hallucination signal
    no_speech = sum(s.no_speech_prob for s in segments) / len(segments)
    logprob = sum(s.avg_logprob for s in segments) / len(segments)
    return not (no_speech > 0.6 and logprob < -0.8)
