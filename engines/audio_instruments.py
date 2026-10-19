"""Reproducible original score and event effects for the 20-second KOSPI demo.

Only numpy, the Python standard library and FFmpeg are required. All sound is
mathematically synthesized here; no samples, third-party recordings or voices.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import shutil
import subprocess
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "audio"
SR = 48_000
DURATION = 20.0
N = round(SR * DURATION)
BPM = 120
SEED = 20261018
RNG = np.random.default_rng(SEED)
FFMPEG_DEFAULT = "ffmpeg"
EXPECTED_CUES = {
    "low_point_accent": 6,
    "recovery_build": 8,
    "3000_break_accent": 16,
    "closing_accent": 19,
}
CUE_LABELS = {
    "low_point_accent": "low point / restrained low impact",
    "recovery_build": "recovery / ostinato opens",
    "3000_break_accent": "breakthrough / rounded impact and soft metallic ring",
    "closing_accent": "resolution / final pluck",
}
PROJECT_NOTE = "Read and validated the shared project.audio.cues by kind; the existing arrangement requires low point / recovery / breakthrough / closing at 6 / 8 / 16 / 19 seconds."


def read_project_cues(project: Path) -> dict[str, float]:
    if not project.is_file():
        raise FileNotFoundError(f"Required shared cue configuration is missing: {project}")
    data = json.loads(project.read_text(encoding="utf-8-sig"))
    cue_times: dict[str, float] = {}
    for cue in data["audio"]["cues"]:
        kind = cue["kind"]
        if kind in EXPECTED_CUES:
            if kind in cue_times:
                raise ValueError(f"Duplicate audio cue: {kind}")
            cue_times[kind] = float(cue["time"])
    if cue_times != EXPECTED_CUES:
        raise ValueError("Shared audio cues differ from 6 / 8 / 16 / 19 seconds; re-arrange the score before generating audio.")
    return cue_times


def hz(midi: float) -> float:
    return 440.0 * 2.0 ** ((midi - 69.0) / 12.0)


def envelope(n: int, attack: float, release: float, decay: float = 0.0) -> np.ndarray:
    a = np.ones(n, dtype=np.float64)
    na, nr = min(round(attack * SR), n), min(round(release * SR), n)
    if na:
        a[:na] *= np.sin(np.linspace(0, math.pi / 2, na)) ** 2
    if nr:
        a[-nr:] *= np.cos(np.linspace(0, math.pi / 2, nr)) ** 2
    if decay:
        a *= np.exp(-np.arange(n) / SR * decay)
    return a


def add(bus: np.ndarray, sound: np.ndarray, start: float, gain: float = 1.0, pan: float = 0.0) -> None:
    i = round(start * SR)
    source_i = max(0, -i)
    i = max(0, i)
    length = min(len(sound) - source_i, len(bus) - i)
    if length <= 0:
        return
    sound = sound[source_i:source_i + length]
    if sound.ndim == 2:
        bus[i:i + length] += sound * gain
    else:
        angle = (pan + 1) * math.pi / 4
        bus[i:i + length, 0] += sound * gain * math.cos(angle)
        bus[i:i + length, 1] += sound * gain * math.sin(angle)


def band_noise(seconds: float, low: float, high: float) -> np.ndarray:
    count = round(seconds * SR)
    x = RNG.normal(0, 1, count)
    spectrum = np.fft.rfft(x)
    freqs = np.fft.rfftfreq(count, 1 / SR)
    # Soft spectral edges avoid the ringing of a rectangular FFT filter.
    lowpass = 1 / np.sqrt(1 + (freqs / high) ** 12)
    highpass = 1 / np.sqrt(1 + (low / np.maximum(freqs, .01)) ** 8)
    y = np.fft.irfft(spectrum * lowpass * highpass, n=count)
    return y / max(float(np.std(y)), 1e-9)


def pad(chord: list[int], seconds: float, brightness: float = 1) -> np.ndarray:
    count = round(seconds * SR)
    t = np.arange(count) / SR
    stereo = np.zeros((count, 2))
    for j, note in enumerate(chord):
        freq = hz(note)
        for channel, detune in enumerate((-.0017, .0017)):
            voice = np.zeros(count)
            for harmonic in range(1, 10):
                amplitude = math.exp(-harmonic / (2.5 * brightness)) / harmonic
                voice += amplitude * np.sin(2 * math.pi * freq * (1 + detune) * harmonic * t + j * .731)
            # Slow, subtle timbre motion; the chord remains a stable reference.
            voice *= .92 + .08 * np.sin(2 * math.pi * .24 * t + j)
            stereo[:, channel] += voice / len(chord)
    return stereo * envelope(count, .55, 1.15)[:, None]


def pluck(note: int, seconds: float = .72, bright: float = 1) -> np.ndarray:
    count = round(seconds * SR)
    t = np.arange(count) / SR
    freq = hz(note)
    sig = np.sin(2 * math.pi * freq * t) * np.exp(-4.7 * t)
    sig += .35 * np.sin(2 * math.pi * freq * 2 * t) * np.exp(-8 * t)
    sig += .13 * bright * np.sin(2 * math.pi * freq * 3 * t) * np.exp(-12 * t)
    return sig * envelope(count, .005, .16)


def bass(note: int, seconds: float = .45) -> np.ndarray:
    count = round(seconds * SR)
    t = np.arange(count) / SR
    freq = hz(note)
    sig = np.sin(2 * math.pi * freq * t)
    sig += .29 * np.sin(2 * math.pi * freq * 2 * t) + .11 * np.sin(2 * math.pi * freq * 3 * t)
    return np.tanh(sig * 1.1) * envelope(count, .008, .15, 2.5)


def drum(kind: str) -> np.ndarray:
    if kind == "kick":
        count = round(.4 * SR)
        t = np.arange(count) / SR
        phase = 2 * math.pi * (45 * t + 55 * .025 * (1 - np.exp(-t / .025)))
        sig = np.sin(phase) * np.exp(-t * 13)
        return sig * envelope(count, .003, .06)
    if kind == "snare":
        sig = band_noise(.25, 250, 2400)
        t = np.arange(len(sig)) / SR
        sig = .65 * sig + .25 * np.sin(2 * math.pi * 185 * t)
        return sig * envelope(len(sig), .003, .09, 21)
    sig = band_noise(.10, 1800, 5500)
    return sig * envelope(len(sig), .002, .03, 55)


def impact(seconds: float = 2.3, metal: bool = False) -> np.ndarray:
    count = round(seconds * SR)
    t = np.arange(count) / SR
    # Rounded low impact and short, band-limited body; no alarm-like sweep.
    body = np.sin(2 * math.pi * (43 * t + 34 * .06 * (1 - np.exp(-t / .06)))) * np.exp(-3.7 * t)
    body += .22 * band_noise(seconds, 90, 950) * np.exp(-8 * t)
    if metal:
        for freq, amp, decay in ((587.33, .21, 1.9), (880, .12, 2.7), (1318.5, .06, 4.3), (1760, .025, 6)):
            body += amp * np.sin(2 * math.pi * freq * t) * np.exp(-decay * t)
    return body * envelope(count, .004, .4)


def whoosh(seconds: float) -> np.ndarray:
    sig = band_noise(seconds, 250, 2600)
    env = np.sin(np.linspace(0, math.pi / 2, len(sig))) ** 3
    return sig * env * envelope(len(sig), .10, .035) * .12


def echo(bus: np.ndarray, delay: float, gain: float, cross: bool = True) -> np.ndarray:
    count = round(delay * SR)
    copy = bus.copy()
    for repeat in range(1, 4):
        offset = count * repeat
        if offset >= N:
            break
        tail = copy[:-offset]
        if cross and repeat % 2:
            tail = tail[:, ::-1]
        bus[offset:] += tail * gain ** repeat
    return bus


def save_wav(path: Path, audio: np.ndarray) -> None:
    if not np.isfinite(audio).all():
        raise ValueError("Nonfinite audio sample")
    if np.max(np.abs(audio)) >= 1:
        raise ValueError(f"PCM clipping before writing {path.name}")
    pcm = np.round(audio * 32767).astype("<i2")
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(2)
        handle.setsampwidth(2)
        handle.setframerate(SR)
        handle.writeframes(pcm.tobytes())


def ffmpeg_run(ffmpeg: str, args: list[str], log: str) -> str:
    result = subprocess.run([ffmpeg, "-hide_banner", "-nostdin", *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    (OUT / log).write_text(result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"FFmpeg failed; see {OUT / log}: {result.stderr[-1200:]}")
    return result.stderr


def json_block(log: str) -> dict:
    matches = re.findall(r'\{\s*"input_i".*?\}', log, flags=re.S)
    if not matches:
        raise ValueError("Missing FFmpeg loudnorm JSON result")
    return json.loads(matches[-1])

