"""Original, deterministic score and event effects for the cinematic120 preset.

Reuses the approved demo's synthesis instruments, but composes a continuous
60-bar arrangement. No samples, recordings, voices, or external music.
"""
from __future__ import annotations

import argparse
from datetime import date
import hashlib
import json
import math
import re
import subprocess
import wave
from pathlib import Path

import numpy as np

import audio_instruments as instruments

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "audio" / "full"
SR = 48000
DURATION = 120.0
N = int(SR * DURATION)
SEED = 20261018
FFMPEG = str(instruments.FFMPEG_DEFAULT)

# These are the arrival/transition times in the approved shared film timeline.
CUES = [
    (0, "slot_01_opening", "dark opening"),
    (11, "slot_02_high", "early high"),
    (20, "slot_03_low", "first low / low impact"),
    (22, "slot_04_recovery", "first recovery resumes"),
    (28, "slot_05_recovery", "recovery accent"),
    (49, "slot_06_breakthrough", "first breakthrough"),
    (55, "slot_07_low", "second low / contracted pulse"),
    (57, "slot_08_recovery", "second recovery resumes"),
    (60, "slot_09_high", "middle high"),
    (65, "slot_10_high", "middle high / continuation"),
    (78, "slot_11_low", "third low / restrained low impact"),
    (80, "slot_12_recovery", "third recovery / ostinato opens"),
    (88, "slot_13_breakthrough", "middle breakthrough"),
    (91, "slot_14_high", "high / continues onward"),
    (97, "slot_15_low", "fourth low"),
    (98, "slot_16_recovery", "fourth recovery resumes"),
    (104, "slot_17_breakthrough", "late breakthrough"),
    (109, "slot_18_breakthrough", "late breakthrough / continuation"),
    (113, "slot_19_high", "late high"),
    (117, "slot_20_closing", "cutoff / full-history resolution"),
]

CHORDS = {
    "Dm": [50, 57, 62, 65, 69],
    "Bb": [46, 53, 58, 62, 65],
    "Gm": [43, 50, 55, 58, 62],
    "As": [45, 52, 57, 62, 64],
    "F": [48, 53, 57, 60, 65],
    "C": [48, 55, 60, 64, 67],
}
PROGRESSION = [
    "Dm", "Bb", "Gm", "As", "Dm", "Gm", "Bb", "Dm",  # 0-32
    "Bb", "F", "Gm", "As", "Dm", "Bb", "Gm", "Dm",  # 32-64
    "Bb", "As", "Dm", "Bb", "Gm", "As", "Dm", "Bb",  # 64-96
    "Gm", "As", "Dm", "F", "As", "Dm",                  # 96-120
]


def energy(t: float) -> float:
    """A deliberately shaped score, with room around the film's low-point holds."""
    knots = [(0, .35), (3, .53), (11, .70), (12, .60), (19.5, .77),
             (20, .28), (22, .34), (28, .68), (29, .57), (41, .67),
             (49, .90), (50, .66), (54.8, .80), (55, .27), (57, .43),
             (60, .77), (61, .62), (65, .80), (66, .68), (72, .63),
             (74, .72), (77.8, .82), (78, .28), (80, .58), (88, .98),
             (90, .84), (91, .88), (92, .67), (96.8, .77), (97, .33),
             (98, .53), (104, .92), (105, .77), (109, .96), (110, .83),
             (113, 1.0), (114, .80), (116.7, .68), (117, .40), (120, .10)]
    return float(np.interp(t, [k[0] for k in knots], [k[1] for k in knots]))


def save_wav24(path: Path, audio: np.ndarray) -> None:
    if not np.isfinite(audio).all() or np.max(np.abs(audio)) >= 1:
        raise ValueError(f"Invalid or clipping audio: {path}")
    pcm = np.rint(audio * 8388607).astype("<i4").reshape(-1)
    packed = np.empty((len(pcm), 3), dtype=np.uint8)
    packed[:, 0] = pcm & 255
    packed[:, 1] = (pcm >> 8) & 255
    packed[:, 2] = (pcm >> 16) & 255
    with wave.open(str(path), "wb") as h:
        h.setnchannels(2)
        h.setsampwidth(3)
        h.setframerate(SR)
        h.writeframes(packed.tobytes())


def ffmpeg_run(ffmpeg: str, args: list[str], logfile: str) -> str:
    p = subprocess.run([ffmpeg, "-hide_banner", "-nostdin", *args], capture_output=True,
                       text=True, encoding="utf-8", errors="replace")
    (OUT / logfile).write_text(p.stderr, encoding="utf-8")
    if p.returncode:
        raise RuntimeError(f"FFmpeg failed: {p.stderr[-1600:]}")
    return p.stderr


def loudnorm_json(log: str) -> dict:
    values = re.findall(r'\{\s*"input_i".*?\}', log, re.S)
    if not values:
        raise ValueError("Missing loudnorm statistics")
    return json.loads(values[-1])


def make_score() -> tuple[np.ndarray, np.ndarray]:
    instruments.RNG = np.random.default_rng(SEED)
    music = np.zeros((N, 2), dtype=np.float32)
    effects = np.zeros((N, 2), dtype=np.float32)
    melody = np.zeros((N, 2), dtype=np.float32)
    add = instruments.add

    # Overlapping two-bar chords are newly voiced across all 60 bars. The final
    # resolution starts at 117s, while the preceding A-sus chord dissolves under it.
    for phrase, name in enumerate(PROGRESSION):
        start = max(0, phrase * 4 - .25)
        duration = min(4.8, DURATION - start)
        e = energy(start + 1.5)
        notes = CHORDS[name][:]
        if phrase >= 20:
            notes.append(notes[-1] + 12)
        add(music, instruments.pad(notes, duration, .65 + .55 * e), start, .20 + .08 * e)
    add(music, instruments.pad([50, 57, 62, 65, 69, 74], 3.3, .8), 116.7, .29)

    kick, snare, hat = (instruments.drum(kind) for kind in ("kick", "snare", "hat"))
    holds = [(20, 22), (55, 57), (78, 80), (97, 98)]
    for beat in range(234):
        t = beat * .5
        name = PROGRESSION[min(int(t // 4), len(PROGRESSION) - 1)]
        root = CHORDS[name][0] - 12
        e = energy(t)
        contracted = any(a <= t < b for a, b in holds)
        if contracted:
            if beat % 2 == 0:
                add(music, instruments.bass(root, .65), t, .10)
            continue
        add(music, instruments.bass(root, .45), t, .12 + .075 * e)
        if beat % 2 == 0:
            add(music, kick, t, .23 * e)
        if t >= 3 and beat % 4 == 2:
            add(music, snare, t, .066 * e, -.12)
        # Opening and crisis passages retain space; later recoveries gain eighths.
        if t >= 22 and e > .53:
            add(music, hat, t + .25, .022 * e, .24 if beat % 2 else -.24)
        if e > .86 and beat % 4 == 3:
            add(music, kick, t + .25, .07)

    # D-natural-minor melodic vocabulary: descent at each crisis, rising stereo
    # figures during recoveries. Each section has a different register and shape.
    desc = [74, 72, 69, 67, 65, 62, 60, 57]
    for start, end, gain in [(12, 20, .052), (50, 55, .055), (74, 78, .065),
                             (92, 97, .052), (114, 117, .046)]:
        steps = round((end - start) * 4)
        for step in range(steps):
            note = desc[min(7, int(step * 8 / steps))]
            add(melody, instruments.pluck(note, .65, .65), start + step * .25,
                gain, -.25 if step % 2 else .25)
    rising = {"Dm": [62, 69, 74, 77, 76, 74, 69, 74],
              "Bb": [58, 65, 70, 74, 72, 70, 65, 70],
              "Gm": [55, 62, 67, 70, 67, 62, 65, 70],
              "As": [57, 64, 69, 74, 72, 69, 64, 69],
              "F": [60, 65, 69, 72, 69, 65, 67, 72],
              "C": [60, 64, 67, 72, 74, 72, 67, 64]}
    sections = [(3, 11, .048), (22, 28, .055), (29, 49, .052), (57, 65, .058),
                (66, 74, .052), (80, 90, .064), (90, 92, .060), (98, 113, .060)]
    for start, end, gain in sections:
        steps = round((end - start) * 4)
        for step in range(steps):
            t = start + .25 * step
            name = PROGRESSION[min(int(t // 4), 29)]
            note = rising[name][step % 8]
            if start < 72 and step % 16 >= 8:
                note -= 12
            add(melody, instruments.pluck(note, .75, .7 + .35 * energy(t)), t,
                gain * (.70 + .40 * energy(t)), -.32 if step % 2 else .32)
    instruments.echo(melody, .375, .27)
    music += melody
    del melody

    # Sparse airy high chords mark three climaxes, never a continuous piercing layer.
    for start, dur, gain in [(46.75, 3.7, .045), (86.75, 4.5, .067),
                             (107.75, 5.0, .052)]:
        add(music, instruments.pad([74, 77, 81], dur, .65), start, gain)
    add(music, instruments.pluck(74, 2.1, .4), 117, .080, -.15)
    add(music, instruments.pluck(69, 1.8, .4), 117.5, .056, .15)
    add(music, instruments.pluck(62, 1.7, .4), 118, .050)

    # Every effect corresponds to a shared film event. At 91s the accent carries
    # onward, without the old demo's end fade or closing cadence.
    low_times = [20, 55, 78, 97]
    for t in low_times:
        add(effects, instruments.whoosh(.65), t - .65, .40, -.10)
        add(effects, instruments.impact(1.9), t, .25)
    for t in [22, 57, 80, 98]:
        add(effects, instruments.whoosh(.85), t - .85, .47, .12)
        add(effects, instruments.pluck(62, 1.3, .5), t, .13, -.10)
    for t, gain in [(49, .34), (88, .43), (104, .32), (109, .35), (113, .40)]:
        add(effects, instruments.whoosh(1.05), t - 1.05, .60 if t != 88 else .70)
        add(effects, instruments.impact(2.6, metal=True), t, gain)
        add(effects, instruments.pluck(81, 1.8, .4), t + .015, .07, .22)
    for t in [11, 28, 60, 65, 91]:
        add(effects, instruments.pluck(74, .9, .5), t, .065)
    add(effects, instruments.impact(2.7), 117, .17)
    add(effects, instruments.pluck(74, 1.7, .45), 117, .075)
    instruments.echo(effects, .19, .12)

    # Gentle onset, three-second final resolution, exact 200ms end fade.
    fade = np.ones(N, dtype=np.float32)
    intro = round(.03 * SR)
    fade[:intro] = np.sin(np.linspace(0, math.pi / 2, intro)) ** 2
    start = round(117 * SR)
    fade[start:] *= np.linspace(1, .35, N - start)
    count = round(.2 * SR)
    fade[-count:] *= np.cos(np.linspace(0, math.pi / 2, count)) ** 2
    music *= fade[:, None]
    effects *= fade[:, None]
    return music, effects


def main() -> None:
    p = argparse.ArgumentParser()
    global OUT
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--ffmpeg", default=FFMPEG)
    p.add_argument("--project", type=Path, required=True)
    args = p.parse_args()
    OUT = args.output.resolve()
    OUT.mkdir(parents=True, exist_ok=True)
    project_sha = hashlib.sha256(args.project.read_bytes()).hexdigest() if args.project.exists() else None
    shared_cues = None
    if args.project.exists():
        config = json.loads(args.project.read_text(encoding="utf-8-sig"))
        shared_cues = config.get("audio", {}).get("cues")
        # The locked demo anchors must remain exact even if the shared event table
        # also carries other SFX, annotation and camera events.
        if shared_cues:
            times = [float(c.get("time", c.get("seconds", -1))) for c in shared_cues]
            # Recovery openings can be the end of a shared hold interval rather
            # than a second explicit SFX cue entry.
            times += [float(segment[key]) for segment in config.get("timeline", [])
                      for key in ("start", "end") if key in segment]
            for required in [cue[0] for cue in CUES]:
                if required not in times:
                    raise ValueError(f"Shared audio cues omit locked anchor {required}s")

    music, effects = make_score()
    raw = music + effects
    scale = min(1, .78 / max(float(np.max(np.abs(raw))), 1e-9))
    music *= scale
    effects *= scale
    raw *= scale
    for name, audio in [("music.wav", music), ("sfx.wav", effects), ("mix_raw.wav", raw)]:
        save_wav24(OUT / name, audio)
    raw_peak, raw_rms = float(np.max(np.abs(raw))), float(np.sqrt(np.mean(raw ** 2)))
    del music, effects, raw

    target = "loudnorm=I=-16:TP=-1.3:LRA=8"
    first = loudnorm_json(ffmpeg_run(args.ffmpeg, ["-i", str(OUT / "mix_raw.wav"), "-af",
        target + ":print_format=json", "-f", "null", "-"], "loudnorm_pass1.log"))
    measured = ":".join([f"measured_I={first['input_i']}", f"measured_TP={first['input_tp']}",
        f"measured_LRA={first['input_lra']}", f"measured_thresh={first['input_thresh']}",
        f"offset={first['target_offset']}"])
    second = loudnorm_json(ffmpeg_run(args.ffmpeg, ["-y", "-i", str(OUT / "mix_raw.wav"), "-af",
        target + ":" + measured + ":linear=true:print_format=json", "-ar", str(SR), "-ac", "2",
        "-c:a", "pcm_s24le", str(OUT / "mix.wav")], "loudnorm_pass2.log"))
    verified = loudnorm_json(ffmpeg_run(args.ffmpeg, ["-i", str(OUT / "mix.wav"), "-af",
        target + ":print_format=json", "-f", "null", "-"], "loudness_master.log"))
    ffmpeg_run(args.ffmpeg, ["-y", "-i", str(OUT / "mix.wav"), "-c:a", "aac", "-b:a", "256k",
        "-ar", str(SR), "-ac", "2", str(OUT / "preview.m4a")], "aac_encode.log")
    aac = loudnorm_json(ffmpeg_run(args.ffmpeg, ["-i", str(OUT / "preview.m4a"), "-af",
        target + ":print_format=json", "-f", "null", "-"], "loudness_aac.log"))
    if abs(float(aac["input_i"]) + 16) > .5 or float(aac["input_tp"]) > -1:
        raise ValueError(f"AAC master failed loudness/true-peak: {aac}")

    # Capture the final shared-table provenance after the potentially long render.
    # The film build may add non-audio descriptions while synthesis is running.
    if args.project.exists():
        project_sha = hashlib.sha256(args.project.read_bytes()).hexdigest()
        shared_cues = json.loads(args.project.read_text(encoding="utf-8-sig")).get("audio", {}).get("cues")
    manifest = {
        "title": config.get("name", "Across the Years") + " — original synthesized score",
        "created_date": date.today().isoformat(), "duration_seconds": 120, "sample_rate": SR,
        "channels": 2, "bpm": 120, "bars": 60, "key": "D minor", "seed": SEED,
        "voiceover": False,
        "composition": "Continuous original mathematical synthesis. Reuses the approved demo's instrument functions; independently arranged 120-second harmony, rhythm, motifs and event effects. No external samples, recordings, voices or existing music.",
        "project": args.project.name, "project_sha256": project_sha, "shared_audio_cues": shared_cues,
        "cues": [{"seconds": t, "kind": kind, "label": next((e["label"] for e in config["events"] if abs(float(e["arrival"])-t)<.01), label)} for t, kind, label in CUES],
        "structure": [
            {"range": [0, 22], "music": "dark entrance, growing pulse, descending crisis motif and low-point contraction"},
            {"range": [22, 57], "music": "recovery, two-bar harmonic phrases, first high climax and restrained financial-crisis low"},
            {"range": [57, 72], "music": "renewed rising figures, gradual transition into the approved demo motif"},
            {"range": [72, 92], "music": "approved timbres: descent, low-point space, rising ostinato, breakthrough; fixed-preset continuation"},
            {"range": [92, 117], "music": "new descent and expanding recovery, successive threshold/high accents, falling motif"},
            {"range": [117, 120], "music": "D-minor resolution, full-history coda, 200ms endpoint fade"}],
        "mastering": {"method": "FFmpeg loudnorm two pass", "target": {"integrated_lufs": -16, "true_peak_dbtp": -1.3, "lra_lu": 8},
            "pass1": first, "pass2": second, "master_measurement": verified, "aac_measurement": aac},
        "stem_gain": "24-bit music.wav and sfx.wav sum to mix_raw.wav within quantization; mix.wav is the mastered version.",
        "technical_validation": {"finite_samples": True, "raw_sample_peak": raw_peak, "raw_rms": raw_rms,
            "full_length_decode": True, "final_fade_seconds": .2, "semantic_listening_completed": False},
        "files": []}
    for name in ["music.wav", "sfx.wav", "mix_raw.wav", "mix.wav", "preview.m4a"]:
        path = OUT / name
        entry = {"name": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        if path.suffix == ".wav":
            with wave.open(str(path), "rb") as h:
                entry.update(sample_rate=h.getframerate(), channels=h.getnchannels(),
                    frames=h.getnframes(), duration_seconds=h.getnframes()/h.getframerate(), pcm_bits=h.getsampwidth()*8)
        manifest["files"].append(entry)
    (OUT / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"out": str(OUT), "aac_lufs": aac["input_i"], "aac_true_peak_dbtp": aac["input_tp"],
        "master_lufs": verified["input_i"], "master_true_peak_dbtp": verified["input_tp"], "files": [e["name"] for e in manifest["files"]]}))


if __name__ == "__main__":
    main()
