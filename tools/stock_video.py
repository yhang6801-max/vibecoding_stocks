"""Windows stock-history film templates: offline CSV to reviewed H.264 video."""
from __future__ import annotations
import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import html
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

from media import choose_encoder, ffmpeg_path, powershell, run
from project import ROOT, digest, load_project, prepare, read_json, write_json


def doctor(args):
    executable = ffmpeg_path(args.ffmpeg)
    shell = powershell()
    result = run([shell, "-NoProfile", "-Command", "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
        "$OutputEncoding = [Console]::OutputEncoding; Add-Type -AssemblyName System.Drawing.Common; "
        "([System.Drawing.Text.InstalledFontCollection]::new()).Families.Name | ConvertTo-Json -Compress"],
        capture_output=True, text=True, encoding="utf-8")
    fonts = json.loads(result.stdout.lstrip("\ufeff"))
    checks = {"windows": os.name == "nt", "python_3_11_or_newer": sys.version_info >= (3, 11),
              "numpy": importlib.util.find_spec("numpy") is not None,
              "chinese_font": any("Microsoft YaHei" in f for f in fonts),
              "number_font": "Bahnschrift" in fonts}
    selected = choose_encoder(executable, "auto")
    print(json.dumps({"checks": checks, "ffmpeg": str(executable), "encoder_auto": selected,
                      "ready": all(checks.values())}, ensure_ascii=False, indent=2))
    if not all(checks.values()):
        raise RuntimeError("环境检查未通过，请按 docs/quickstart.md 补齐依赖")


def initialize(args):
    folder = args.output.expanduser().resolve()
    if folder.exists() and any(folder.iterdir()):
        raise FileExistsError("初始化目录必须为空，避免覆盖已有项目")
    example = ROOT / "examples" / ("kospi" if args.preset == "cinematic120" else "nikkei")
    folder.mkdir(parents=True, exist_ok=True)
    for filename in ("project.json", "daily.csv", "sources.json"):
        shutil.copyfile(example / filename, folder / filename)
    print(f"项目已创建：{folder / 'project.json'}\n请编辑标的、事件日期、章节、字幕和 publishing，再制作预览。")


def inputs_hash(project, csv_path):
    files = {"project": digest(project), "daily": digest(csv_path)}
    for folder in ("engines", "tools"):
        for path in sorted((ROOT / folder).glob("*")):
            if path.suffix in (".py", ".cs", ".ps1"):
                files[path.relative_to(ROOT).as_posix()] = digest(path)
    return files


def audio(config, work, output, ffmpeg):
    if config["preset"] != "cinematic120":
        return None
    target = output / "audio"
    fingerprint = {"project": digest(work / "project.json"),
                   "score": digest(ROOT / "engines/cinematic_audio.py"),
                   "instruments": digest(ROOT / "engines/audio_instruments.py")}
    saved = target / "inputs.json"
    if not saved.exists() or read_json(saved) != fingerprint or not (target / "mix.wav").is_file():
        print("合成原创配乐与事件音效…", flush=True)
        run([sys.executable, ROOT / "engines/cinematic_audio.py", "--project", work / "project.json",
             "--output", target, "--ffmpeg", ffmpeg])
        write_json(saved, fingerprint)
    return target / "mix.wav"


def render_range(config, work, destination, ffmpeg, encoder, start, count):
    destination.mkdir(parents=True, exist_ok=True)
    stem = f"chunk_{start:06d}.mp4"
    command = [powershell(), "-NoProfile", "-File"]
    if config["preset"] == "cinematic120":
        command += [ROOT / "engines/render_cinematic.ps1", "-Data", work / "data.json", "-Project", work / "project.json",
                    "-OutputDir", destination, "-VideoOnly"]
    else:
        command += [ROOT / "engines/render_classic.ps1", "-Data", work / "data.json", "-Output", destination / stem,
                    "-ContentScale", config.get("render", {}).get("content_scale", 1)]
    command += ["-FFmpeg", ffmpeg, "-Encoder", encoder, "-StartFrame", start, "-RenderFrames", count]
    with (destination / "render.log").open("w", encoding="utf-8") as log:
        result = subprocess.run([str(item) for item in command], stdout=log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if result.returncode:
        raise RuntimeError((destination / "render.log").read_text(encoding="utf-8", errors="replace")[-6000:])
    video = destination / stem
    states = destination / "frame_states.jsonl"
    if not video.is_file() or not states.is_file():
        raise RuntimeError(f"渲染未生成视频或报价记录：{destination}")
    return start, video, states


def merge_states(paths, target, first, expected_count):
    next_frame = first
    with target.open("w", encoding="utf-8", newline="\n") as merged:
        for path in paths:
            for line in path.read_text(encoding="utf-8-sig").splitlines():
                if not line.strip():
                    continue
                state = json.loads(line)
                if state["frame"] != next_frame:
                    raise ValueError(f"分段状态不连续：期望 {next_frame}，实际 {state['frame']}")
                merged.write(json.dumps(state, ensure_ascii=False, separators=(",", ":")) + "\n")
                next_frame += 1
    if next_frame != first + expected_count:
        raise ValueError("分段状态总帧数错误")


def make_review(config, video, output, ffmpeg, start_seconds, duration):
    images = output / "review_frames"
    images.mkdir(exist_ok=True)
    moments = [start_seconds, start_seconds + max(0, duration - 1 / 30)]
    moments += [float(e["arrival"]) + min(float(e["hold"]) / 2, .5) for e in config["events"]
                if start_seconds <= float(e["arrival"]) < start_seconds + duration]
    moments = sorted(set(round(t, 4) for t in moments))
    if len(moments) > 14:
        moments = [moments[round(i * (len(moments) - 1) / 13)] for i in range(14)]
    cards = []
    scale = "960:540" if config["preset"] == "cinematic120" else "270:480"
    for index, stamp in enumerate(moments):
        position = min(max(0, stamp - start_seconds), max(0, duration - 1 / 30))
        relative = f"review_frames/sample_{index:02d}.png"
        run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-ss", f"{position:.6f}", "-i", video,
             "-frames:v", "1", "-vf", "scale=" + scale, output / relative], stdout=subprocess.DEVNULL)
        cards.append(f'<figure><img src="{relative}"><figcaption>{html.escape(str(stamp))} 秒</figcaption></figure>')
    buttons = "".join(f'<button onclick="v.currentTime={float(c["start"])-start_seconds};v.play()">{html.escape(c["title"])}</button>'
                      for c in config.get("chapters", []) if start_seconds <= float(c["start"]) < start_seconds + duration)
    page = f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(config['name'])}</title><style>body{{margin:0;background:#070e1b;color:#edf1f5;font:16px system-ui}}main{{max-width:1100px;margin:auto;padding:32px}}h1{{color:#d7bd80}}video{{width:100%;max-height:75vh;background:#000}}button{{padding:10px;margin:5px;background:#182338;color:#fff;border:1px solid #526078;border-radius:6px}}section{{display:flex;flex-wrap:wrap;gap:14px}}figure{{margin:0;width:calc(50% - 14px)}}img{{width:100%;max-height:450px;object-fit:contain;background:#02050b}}figcaption{{padding:8px;color:#9aa9bb}}a{{color:#d7bd80}}@media(max-width:600px){{figure{{width:100%}}}}</style>
<main><h1>{html.escape(config['name'])}</h1><p>{html.escape(config['display']['range_label'])}</p><video id="v" controls playsinline src="{video.name}"></video><nav>{buttons}</nav>
<p><a href="{video.name}" download>下载视频</a> · <a href="verification.json">验收记录</a> · <a href="frame_states.jsonl">逐帧报价记录</a></p><section>{''.join(cards)}</section></main></html>'''
    (output / "review.html").write_text(page, encoding="utf-8")


def build(args):
    started = time.monotonic()
    selected_output = args.output or args.project.resolve().parent / "output"
    project, config, rows, csv_path, work = prepare(args.project, selected_output)
    output = work.parent
    ffmpeg = ffmpeg_path(args.ffmpeg)
    encoder = choose_encoder(ffmpeg, args.encoder)
    fps, total = config["video"]["fps"], config["video"]["frames"]
    preview = args.command == "preview"
    start = round(args.start * fps) if preview else 0
    count = round(args.seconds * fps) if preview else total
    if start < 0 or count < 1 or start + count > total:
        raise ValueError("预览范围超出预设时长")
    music = audio(config, work, output, ffmpeg)
    chunk_size = min(count, int(config.get("render", {}).get("chunk_frames", 300)))
    if chunk_size <= 0:
        raise ValueError("render.chunk_frames 必须为正数")
    ranges = [(frame, min(chunk_size, start + count - frame)) for frame in range(start, start + count, chunk_size)]
    parts = {}
    workers = min(args.workers, len(ranges))
    print(f"{config['name']} · {encoder} · {count} 帧 · {workers} 个渲染进程", flush=True)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        pending = [pool.submit(render_range, config, work, output / "chunks" / f"{first:06d}", ffmpeg, encoder, first, length)
                   for first, length in ranges]
        for future in as_completed(pending):
            first, video, states = future.result()
            parts[first] = (video, states)
            print(f"已完成分段 {first}，{len(parts)}/{len(ranges)}；{time.monotonic()-started:.1f} 秒", flush=True)
    ordered = [parts[first] for first in sorted(parts)]
    merge_states([state for _, state in ordered], output / "frame_states.jsonl", start, count)
    concat = output / "chunks/concat.txt"
    concat.write_text("".join("file '" + video.as_posix().replace("'", "'\\''") + "'\n" for video, _ in ordered), encoding="utf-8")
    video = output / ("preview.mp4" if preview else "video.mp4")
    command = [ffmpeg, "-hide_banner", "-loglevel", "warning", "-y", "-f", "concat", "-safe", "0", "-i", concat]
    if music:
        command += ["-ss", str(start / fps), "-i", music, "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]
    else:
        command += ["-map", "0:v:0", "-an"]
    command += ["-c:v", "copy", "-t", str(count / fps), "-movflags", "+faststart", video]
    run(command)
    record = {"kind": "preview" if preview else "full", "video": video.name, "start_frame": start, "frames": count,
              "fps": fps, "duration": count / fps, "width": config["video"]["width"], "height": config["video"]["height"],
              "encoder": encoder, "audio": bool(music), "input_hashes": inputs_hash(project, csv_path),
              "video_sha256": digest(video), "elapsed_seconds": round(time.monotonic() - started, 2)}
    write_json(output / "build_record.json", record)
    make_review(config, video, output, ffmpeg, start / fps, count / fps)
    from verify import verify
    verify(project, output, ffmpeg)
    print(f"已生成视频和审阅页：{output / 'review.html'}", flush=True)


def covers(args):
    project, config, rows, csv_path, work = prepare(args.project, args.output or args.project.resolve().parent / "output")
    folder = work.parent / "covers"
    run([powershell(), "-NoProfile", "-File", ROOT / "engines/generate_covers.ps1", "-Data", csv_path,
         "-Project", work / "project.json", "-OutputDirectory", folder])
    copy = config["publishing"]
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "标题.txt").write_text(copy["title"] + "\n", encoding="utf-8")
    (folder / "简介.txt").write_text(copy["description"] + "\n", encoding="utf-8")
    (folder / "发布文案.txt").write_text("标题\n" + copy["title"] + "\n\n简介\n" + copy["description"] + "\n", encoding="utf-8")
    print(f"横竖封面和文案：{folder}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("doctor", help="检查 Windows、字体、依赖与编码器")
    p.add_argument("--ffmpeg"); p.set_defaults(action=doctor)
    p = commands.add_parser("init", help="复制包含真实行情的模板项目")
    p.add_argument("--preset", choices=("cinematic120", "classic220"), default="cinematic120")
    p.add_argument("--output", type=Path, required=True); p.set_defaults(action=initialize)
    for name in ("preview", "build"):
        p = commands.add_parser(name)
        p.add_argument("--project", type=Path, required=True)
        p.add_argument("--output", type=Path); p.add_argument("--ffmpeg")
        p.add_argument("--encoder", choices=("auto", "libx264", "h264_nvenc"), default="auto")
        p.add_argument("--workers", type=int, choices=(1, 2, 3), default=3)
        if name == "preview":
            p.add_argument("--seconds", type=float, default=5); p.add_argument("--start", type=float, default=0)
        p.set_defaults(action=build)
    p = commands.add_parser("covers")
    p.add_argument("--project", type=Path, required=True); p.add_argument("--output", type=Path)
    p.set_defaults(action=covers)
    p = commands.add_parser("verify")
    p.add_argument("--project", type=Path, required=True); p.add_argument("--output", type=Path); p.add_argument("--ffmpeg")
    def verify_action(args):
        from verify import verify
        verify(args.project, args.output or args.project.resolve().parent / "output", ffmpeg_path(args.ffmpeg))
    p.set_defaults(action=verify_action)
    args = parser.parse_args()
    try:
        args.action(args)
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as exc:
        print("错误：" + str(exc), file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
