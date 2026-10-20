"""Check the current project's encoded media and actual per-frame quotes."""
from __future__ import annotations
import json
from pathlib import Path
import re
import subprocess

from media import run, probe_media
from fractions import Fraction
import math
from project import audio_enabled, digest, load_project, read_json, write_json


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
    if config.get("audio", {}).get("score") == "external220":
        check("current_external_audio", all(
            record["input_hashes"].get("external_audio_"+key) == digest(project.parent/config["audio"][key])
            for key in ("file", "provenance")), "Audio and provenance hashes match current project")
    metadata = probe_media(ffmpeg, video)
    streams = metadata.get("streams", [])
    videos = [s for s in streams if s.get("codec_type") == "video"]
    audios = [s for s in streams if s.get("codec_type") == "audio"]
    v = videos[0] if videos else {}
    a = audios[0] if audios else {}
    result = run([ffmpeg, "-hide_banner", "-v", "error", "-xerror", "-i", video,
                  "-map", "0:v:0", "-progress", "pipe:1", "-nostats", "-f", "null", "-"],
                 capture_output=True, text=True, encoding="utf-8", errors="replace")
    counts = re.findall(r"^frame=(\d+)$", result.stdout, re.M)
    decoded = int(counts[-1]) if counts else -1
    check("complete_decode_and_frames", decoded == record["frames"], decoded)
    check("H264_and_yuv420p", len(videos) == 1 and v.get("codec_name") == "h264" and v.get("pix_fmt") == "yuv420p", v.get("codec_name"))
    expected_width = round(config["video"]["width"] * record.get("preview_scale", 1))
    expected_height = round(config["video"]["height"] * record.get("preview_scale", 1))
    rate = v.get("avg_frame_rate", "0/1")
    try:
        actual_fps = float(Fraction(rate))
    except (ValueError, ZeroDivisionError):
        actual_fps = 0
    check("canvas_and_fps", v.get("width") == record["width"] == expected_width and
          v.get("height") == record["height"] == expected_height and actual_fps == record["fps"] == config["video"]["fps"],
          [v.get("width"), v.get("height"), rate])
    check("audio_presence", len(audios) == (1 if record["audio"] else 0) and
          record["audio"] == audio_enabled(config), len(audios))
    check("frame_range", record["start_frame"] >= 0 and record["frames"] > 0 and
          record["start_frame"] + record["frames"] <= config["video"]["frames"] and
          (record["kind"] != "full" or (record["start_frame"] == 0 and record["frames"] == config["video"]["frames"])) and
          abs(record["duration"] - record["frames"] / record["fps"]) < 1e-6, [record["start_frame"], record["frames"]])
    duration = float(metadata.get("format", {}).get("duration", -1))
    check("duration", abs(duration - record["duration"]) <= .1, duration)
    check("full_resolution", record["kind"] != "full" or record.get("preview_scale", 1) == 1, record.get("preview_scale", 1))
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
    for event in config["events"]:
        if event["hold"] > 0:
            begin = max(first, math.ceil(event["arrival"] * record["fps"] - 1e-8))
            end = min(first + record["frames"], math.ceil((event["arrival"] + event["hold"]) * record["fps"] - 1e-8))
            event_holds[event["id"]]["expected"] = max(0, end - begin)
    check("event_holds", all(e["frames"] == e["expected"] and e["correct"] for e in event_holds.values()), event_holds)
    if record["kind"] == "full":
        check("final_trading_day", last_date == rows[-1]['date'], last_date)
    loudness = None
    if record["audio"]:
        check("AAC_48kHz_stereo", a.get("codec_name") == "aac" and a.get("sample_rate") == "48000" and a.get("channels") == 2, "48kHz stereo AAC")
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
