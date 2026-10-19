"""Check the current project's encoded media and actual per-frame quotes."""
from __future__ import annotations
import json
from pathlib import Path
import re
import subprocess

from media import run
from project import digest, load_project, read_json, write_json


def verify(project: Path, output: Path, ffmpeg):
    project, config, rows, csv_path = load_project(project)
    output = output.resolve()
    record = read_json(output / "build_record.json")
    video = output / record["video"]
    checks = []
    def check(name, condition, detail):
        checks.append({"name": name, "passed": bool(condition), "detail": detail})
    check("encoded_video_hash", digest(video) == record["video_sha256"], record["video_sha256"])
    check("current_project_and_csv", record["input_hashes"]["project"] == digest(project) and record["input_hashes"]["daily"] == digest(csv_path), "Inputs match current project")
    result = run([ffmpeg, "-hide_banner", "-i", video, "-map", "0:v:0", "-f", "null", "-"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    log = result.stderr
    counts = re.findall(r"frame=\s*(\d+)", log)
    decoded = int(counts[-1]) if counts else -1
    check("complete_decode_and_frames", decoded == record["frames"], decoded)
    check("H264_and_yuv420p", "Video: h264" in log and "yuv420p" in log, "H.264 / yuv420p")
    check("canvas_and_fps", f"{record['width']}x{record['height']}" in log and re.search(r"\b30 fps\b", log), [record["width"], record["height"], 30])
    check("audio_presence", ("Audio: aac" in log) == record["audio"], record["audio"])
    durations = re.findall(r"Duration: (\d+):(\d+):(\d+\.\d+)", log)
    duration = sum(float(t) * factor for t, factor in zip(durations[0], (3600, 60, 1))) if durations else -1
    check("duration", abs(duration - record["duration"]) <= .1, duration)
    by_date = {row["date"]: row["close"] for row in rows}
    count, correct, contiguous, no_future = 0, True, True, True
    event_holds = {e['id']: {'frames': 0, 'correct': True} for e in config['events'] if e['hold'] > 0}
    last_date = None
    first = record["start_frame"]
    with (output / "frame_states.jsonl").open(encoding="utf-8-sig") as source:
        for line in source:
            if not line.strip(): continue
            state = json.loads(line)
            contiguous &= state["frame"] == first + count
            correct &= state.get("date") in by_date and abs(state.get("close", -1) - by_date.get(state.get("date"), -999)) < .0001
            no_future &= isinstance(state.get("visible_last_date"), str) and state["visible_last_date"] <= state["date"]
            last_date = state.get("date")
            seconds = state["frame"] / record["fps"]
            for event in config["events"]:
                if event['hold'] > 0 and event['arrival'] <= seconds < event['arrival'] + event['hold']:
                    held = event_holds[event['id']]
                    held['frames'] += 1
                    held['correct'] &= state.get('date') == event['date']
            count += 1
    check("contiguous_frame_states", contiguous and count == record["frames"], count)
    check("actual_trading_day_quotes", correct, "Every quoted close matches current CSV")
    check("no_future_chart_data", no_future, "Visible last date never exceeds quote date")
    if record["kind"] == "full":
        check("event_holds", all(e['frames'] > 0 and e['correct'] for e in event_holds.values()), event_holds)
        check("final_trading_day", last_date == rows[-1]['date'], last_date)
    loudness = None
    if record["audio"]:
        check("AAC_48kHz_stereo", "48000 Hz, stereo" in log, "48kHz stereo AAC")
        decoded_audio = run([ffmpeg, "-hide_banner", "-loglevel", "error", "-i", video,
            "-vn", "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1"], capture_output=True)
        audio_seconds = len(decoded_audio.stdout) / (48000 * 2 * 4)
        check("audio_covers_video", abs(audio_seconds - record["duration"]) <= .08, audio_seconds)
        measured = run([ffmpeg, "-hide_banner", "-i", video, "-vn", "-af", "loudnorm=I=-16:TP=-1.3:LRA=8:print_format=json", "-f", "null", "-"], capture_output=True, text=True, encoding="utf-8", errors="replace")
        matches = re.findall(r'\{\s*"input_i".*?\}', measured.stderr, re.S)
        loudness = json.loads(matches[-1]) if matches else None
        check("audio_measured", loudness is not None, loudness)
        if loudness:
            # A short excerpt need not have the integrated loudness of a complete score.
            if record["kind"] == "full":
                check("integrated_loudness", abs(float(loudness["input_i"]) + 16) <= 1, loudness["input_i"])
            check("true_peak", float(loudness["input_tp"]) <= -1, loudness["input_tp"])
    result = {"passed": all(c["passed"] for c in checks), "checks_passed": sum(c["passed"] for c in checks),
              "checks_total": len(checks), "checks": checks, "audio_measurement": loudness,
              "limits": "Technical checks do not substitute for watching review.html and listening to the actual encoded video."}
    write_json(output / "verification.json", result)
    print(json.dumps({"passed": result["passed"], "checks": f"{result['checks_passed']}/{result['checks_total']}"}, ensure_ascii=False), flush=True)
    if not result["passed"]:
        raise ValueError("媒体验收失败，详见 " + str(output / "verification.json"))
    return result
