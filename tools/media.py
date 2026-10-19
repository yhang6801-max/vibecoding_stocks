"""External media tools. No vendor-specific installation assumptions."""
from __future__ import annotations
import os
from pathlib import Path
import shutil
import subprocess


def run(command, **kwargs):
    return subprocess.run([str(value) for value in command], check=True,
                          creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0, **kwargs)


def ffmpeg_path(explicit=None):
    candidate = explicit or os.environ.get("FFMPEG_BIN") or shutil.which("ffmpeg")
    if not candidate:
        raise FileNotFoundError("找不到 FFmpeg：请安装后加入 PATH，或使用 --ffmpeg / FFMPEG_BIN")
    resolved = Path(candidate).expanduser()
    if not resolved.is_file():
        resolved = Path(shutil.which(str(candidate)) or str(candidate))
    return resolved.resolve(strict=True)


def powershell():
    path = shutil.which("pwsh")
    if not path:
        raise FileNotFoundError("需要 PowerShell 7（pwsh）；不是 Windows PowerShell 5.1")
    return path


def choose_encoder(ffmpeg, requested="auto"):
    def available(encoder):
        probe = subprocess.run([str(ffmpeg), "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "color=c=black:s=320x240:r=30", "-frames:v", "1", "-c:v", encoder, "-f", "null", "-"],
            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        return probe.returncode == 0
    candidates = ("h264_nvenc", "libx264") if requested == "auto" else (requested,)
    for encoder in candidates:
        if available(encoder):
            return encoder
    raise RuntimeError(f"编码器实际预检失败（{', '.join(candidates)}），请使用包含 libx264 的完整 FFmpeg 或检查显卡驱动")


def ffprobe_path(ffmpeg):
    sibling = Path(ffmpeg).with_name('ffprobe.exe' if os.name == 'nt' else 'ffprobe')
    candidate = os.environ.get('FFPROBE_BIN') or (str(sibling) if sibling.is_file() else shutil.which('ffprobe'))
    if not candidate:
        raise FileNotFoundError('需要 FFprobe（通常随 FFmpeg 安装），或设置 FFPROBE_BIN')
    return Path(candidate).expanduser().resolve(strict=True)


def probe_media(ffmpeg, video):
    import json
    result = run([ffprobe_path(ffmpeg), '-v', 'error', '-show_streams', '-show_format',
                  '-of', 'json', video], capture_output=True, text=True, encoding='utf-8')
    return json.loads(result.stdout)
