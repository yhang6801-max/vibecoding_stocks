"""Project configuration and offline, immutable daily-close inputs."""
from __future__ import annotations

import copy
import csv
from datetime import date
import hashlib
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PRESETS = {"cinematic120": (1920, 1080, 30, 120), "classic220": (1080, 1920, 30, 220)}


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(path: Path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def daily_csv(path: Path):
    rows = []
    with path.open(encoding="utf-8-sig", newline="") as source:
        reader = csv.DictReader(source)
        if not reader.fieldnames or not {"date", "close"}.issubset(reader.fieldnames):
            raise ValueError("行情 CSV 必须包含 date,close 列")
        for number, row in enumerate(reader, 2):
            try:
                stamp = date.fromisoformat(row["date"])
                value = float(row["close"])
            except (ValueError, TypeError) as exc:
                raise ValueError(f"CSV 第 {number} 行日期或收盘价无效") from exc
            if stamp.isoformat() != row["date"] or not math.isfinite(value) or value <= 0:
                raise ValueError(f"CSV 第 {number} 行须使用 YYYY-MM-DD 和正有限价格")
            if rows and row["date"] <= rows[-1]["date"]:
                raise ValueError(f"CSV 第 {number} 行日期重复或未升序排列")
            rows.append({"date": row["date"], "close": value})
    if len(rows) < 2:
        raise ValueError("至少需要两个实际交易日")
    return rows


def nice_upper(value):
    step = 10 ** math.floor(math.log10(value)) / 2
    return max(step, math.ceil(value * 1.03 / step) * step)


def audio_enabled(config):
    return config["preset"] == "cinematic120" or config.get("audio", {}).get("score") == "energetic220"


def load_project(path: Path):
    path = path.expanduser().resolve(strict=True)
    config = copy.deepcopy(read_json(path))
    preset = config.get("preset")
    if preset not in PRESETS:
        raise ValueError("preset 必须是 cinematic120 或 classic220")
    width, height, fps, duration = PRESETS[preset]
    expected = dict(width=width, height=height, fps=fps, duration=duration, frames=fps * duration)
    if any(config.get("video", {}).get(key, value) != value for key, value in expected.items()):
        raise ValueError(f"{preset} 使用固定规格 {expected}")
    config["video"] = expected
    if preset == "classic220" and config.get("audio", {}).get("score") not in (None, "energetic220"):
        raise ValueError("classic220 audio.score 仅支持 energetic220 或省略为静音")
    csv_path = (path.parent / config["data"]).resolve(strict=True)
    rows = daily_csv(csv_path)
    by_date = {row["date"]: row["close"] for row in rows}
    instrument = config.get("instrument", {})
    for key in ("name", "symbol", "unit"):
        if not str(instrument.get(key, "")).strip():
            raise ValueError(f"instrument.{key} 不可为空")
    events = config.get("events", [])
    if not events:
        raise ValueError("请至少配置一个事件节点")
    ids, previous_date, previous_end = set(), "", 0.0
    for index, event in enumerate(events):
        day = event["date"]
        if day not in by_date:
            raise ValueError(f"事件 {event.get('id', index)} 不是 CSV 中的实际交易日：{day}")
        if day <= previous_date:
            raise ValueError("事件日期必须严格升序，不可重复")
        previous_date = day
        event.setdefault("id", f"N{index + 1:02}")
        if event["id"] in ids:
            raise ValueError("事件 ID 不可重复")
        ids.add(event["id"])
        event["close"] = by_date[day]
        event.setdefault("major", False)
        event.setdefault("low", False)
        arrival = float(event["arrival"])
        hold = float(event.get("hold", 2 if event["major"] else 1))
        event["hold"] = hold
        if not math.isfinite(arrival + hold) or arrival < previous_end - 1e-6 or hold < 0 or arrival + hold > duration + 1e-6:
            raise ValueError(f"事件 {event['id']} 的 arrival/hold 超界或重叠")
        previous_end = arrival + hold
    if preset == "cinematic120":
        if not {"E01", "E02", "E03", "E04"}.issubset(ids):
            raise ValueError("cinematic120 预设保留 E01—E04 叙事槽位，请修改节点日期与文字并保留 ID")
        if events[0]["date"] != rows[0]["date"] or events[0]["arrival"] != 0 or events[-1]["date"] != rows[-1]["date"]:
            raise ValueError("电影感预设首尾事件必须对应 CSV 首尾日期，首事件 arrival 为 0")
        timeline, current_day, current_time = [], rows[0]["date"], 0.0
        for event in events:
            arrival, hold = float(event["arrival"]), float(event["hold"])
            if arrival > current_time:
                timeline.append(dict(start=current_time, end=arrival, **{"from": current_day, "to": event["date"]}, mode="travel"))
            if hold > 0:
                timeline.append(dict(start=arrival, end=arrival + hold, **{"from": event["date"], "to": event["date"]}, mode="hold"))
            current_time, current_day = arrival + hold, event["date"]
        if current_time < duration:
            timeline.append(dict(start=current_time, end=duration, **{"from": current_day, "to": rows[-1]["date"]}, mode="travel"))
        config["timeline"] = timeline
        chart = config["chart"]
        if chart.get("auto_scale", False):
            chart["y_max"] = nice_upper(max(row["close"] for row in rows))
            chart["axis_transitions"] = []
        if chart["y_max"] <= 0 or chart.get("y_min", 0) != 0:
            raise ValueError("电影感图表的 y_min 为 0，y_max 必须大于 0")
        for context in config.get("contexts", []):
            date.fromisoformat(context["date"]); date.fromisoformat(context["until_date"])
        comparisons = config.setdefault("comparisons", {})
        e01 = next(e for e in events if e["id"] == "E01")
        e03 = next(e for e in events if e["id"] == "E03")
        comparisons.setdefault("demo_reference_date", e01["date"])
        comparisons.setdefault("threshold_date", e03["date"])
        comparisons.setdefault("threshold", e03["close"])
        if any(comparisons[key] not in by_date for key in ("demo_reference_date", "threshold_date")):
            raise ValueError("comparisons 中的参照日期必须为实际交易日")
    elif any(float(e["arrival"]) < 3 or float(e["arrival"]) + float(e["hold"]) > 218 for e in events):
        raise ValueError("classic220 的事件须在 3—218 秒内，为开场和全景片尾保留时间")
    first, last = rows[0]["date"], rows[-1]["date"]
    config["full"] = {"duration": duration, "data_start": first, "data_end": last}
    display = config.setdefault("display", {})
    defaults = {"title": config.get("name", instrument["name"]), "subtitle": "起伏、突破与回撤",
                "instrument_label": instrument["name"], "source": config.get("data_source", "用户提供的 CSV"),
                "range_label": f"{first[:4]}—{last[:4]}", "ending": "历史走势回顾", "unit": instrument["unit"]}
    for key, value in defaults.items():
        display.setdefault(key, value)
    publishing = config.setdefault("publishing", {})
    publishing.setdefault("title", config.get("name", instrument["name"]))
    publishing.setdefault("description", f"{instrument['name']} {first}—{last} 历史日收盘走势。")
    publishing.setdefault("cover_lines", [instrument["name"], f"{int(last[:4])-int(first[:4])}年"])
    publishing.setdefault("cover_subtitle", display["subtitle"])
    if len(publishing["title"]) > 30 or len(publishing["description"]) > 1000:
        raise ValueError("发布标题须 ≤30 字符，简介含话题须 ≤1000 字符")
    if len(publishing["cover_lines"]) != 2 or any(not str(s).strip() or len(s) > 10 for s in publishing["cover_lines"]):
        raise ValueError("publishing.cover_lines 须为两行短标题，每行 1—10 字符")
    return path, config, rows, csv_path


def prepare(path: Path, output: Path):
    project, config, rows, csv_path = load_project(path)
    output = output.expanduser().resolve()
    for key in ("text_file", "number_file"):
        if config.get("fonts", {}).get(key):
            config["fonts"][key] = str((project.parent / config["fonts"][key]).resolve(strict=True))
    work = output / "prepared"
    work.mkdir(parents=True, exist_ok=True)
    write_json(work / "project.json", config)
    write_json(work / "data.json", {"daily": rows, "anchors": rows, "events": config["events"], "display": config["display"]})
    return project, config, rows, csv_path, work
