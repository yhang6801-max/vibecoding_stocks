"""Rebuild README SVG layout illustrations from every example CSV row.

These are independently drawn vector illustrations, never MP4 screenshots.
Run with Python from any directory; no graphics package or network is needed.
"""
from __future__ import annotations

import csv
import html
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = Path(__file__).resolve().parent
FONT = "'Microsoft YaHei','Noto Sans CJK SC',sans-serif"


def daily(example: str) -> list[tuple[str, float]]:
    with (ROOT / "examples" / example / "daily.csv").open(encoding="utf-8-sig", newline="") as file:
        return [(r["date"], float(r["close"])) for r in csv.DictReader(file)]


def text(x: float, y: float, value: str, size=24, color="#f0f2f3", weight=400, anchor="start") -> str:
    return (f'<text x="{x}" y="{y}" font-family="{FONT}" font-size="{size}" '
            f'font-weight="{weight}" fill="{color}" text-anchor="{anchor}">{html.escape(value)}</text>')


def chart(rows, x, y, width, height, ymax, stroke=2):
    begin = date.fromisoformat(rows[0][0]).toordinal()
    length = date.fromisoformat(rows[-1][0]).toordinal() - begin
    points = [(x + width * (date.fromisoformat(d).toordinal() - begin) / length,
               y + height * (1 - value / ymax)) for d, value in rows]
    path = "M" + " L".join(f"{px:.2f},{py:.2f}" for px, py in points)
    geometry = []
    for i in range(5):
        gy = y + height * i / 4
        geometry.append(f'<path d="M{x},{gy}H{x + width}" stroke="#26364a" stroke-width="1"/>')
    geometry.append(f'<path d="{path}" fill="none" stroke="#d7bd80" stroke-opacity="0.12" stroke-width="{stroke * 5}" stroke-linejoin="round"/>')
    geometry.append(f'<path d="{path}" fill="none" stroke="#fff0d1" stroke-width="{stroke}" stroke-linejoin="round"/>')
    px, py = points[-1]
    geometry.append(f'<circle cx="{px:.2f}" cy="{py:.2f}" r="7" fill="#d7bd80" fill-opacity=".2"/>')
    geometry.append(f'<circle cx="{px:.2f}" cy="{py:.2f}" r="3.5" fill="#fff0d1"/>')
    return "".join(geometry), points


def shell(width, height, title, body):
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">
<title id="title">{html.escape(title)}</title>
<desc id="desc">使用示例CSV全部真实日收盘重绘的静态版式示意，不是成片解码帧。</desc>
<defs><linearGradient id="bg" x2="1" y2="1"><stop stop-color="#050914"/><stop offset="1" stop-color="#0c1c30"/></linearGradient></defs>
<rect width="100%" height="100%" fill="url(#bg)"/>
{body}
</svg>
'''


def preview(example, title, subtitle, ymax):
    rows = daily(example)
    graph, points = chart(rows, 80, 298, 1120, 310, ymax, 1.4)
    end_date, end_value = rows[-1]
    content = [text(80, 62, "VIBECODING / MARKET HISTORY", 19, "#d7bd80", 600),
               text(80, 154, title, 78, weight=700),
               text(80, 208, subtitle, 27, "#a7b6c9"),
               text(1200, 89, end_date, 22, "#a7b6c9", anchor="end"),
               text(1200, 146, f"{end_value:,.2f}", 43, "#fff0d1", 600, "end"),
               text(1200, 183, "最后交易日收盘 / 点", 18, "#8392a7", anchor="end"), graph]
    low = min(range(len(rows)), key=lambda index: rows[index][1])
    peak = max(range(len(rows)), key=lambda index: rows[index][1])
    for index, label, color, dy in [(low, "区间最低", "#61d7be", -24), (peak, "区间最高", "#d7bd80", -31)]:
        px, py = points[index]
        tx = max(185, min(1090, px))
        content.extend([f'<circle cx="{px:.2f}" cy="{py:.2f}" r="4" fill="{color}"/>',
                        text(tx, max(259, py + dy), f"{label} {rows[index][0]} · {rows[index][1]:,.2f}", 17, color, anchor="middle")])
    content.extend([text(80, 649, rows[0][0][:4], 18, "#8392a7"),
                    text(1200, 649, end_date[:4], 18, "#8392a7", anchor="end"),
                    text(80, 686, f"全部 {len(rows):,} 条日收盘 · 价格指数，不含分红再投资", 16, "#8392a7"),
                    text(1200, 686, "静态版式示意 / 非视频解码帧", 16, "#8392a7", anchor="end")])
    (OUT / f"{example}-preview.svg").write_text(shell(1280, 720, title, "\n".join(content)), encoding="utf-8")


def covers():
    rows = daily("kospi")
    content = [text(48, 59, "同一份真实历史，两种独立封面排版", 31, weight=600),
               text(48, 96, "4:3 / 3:4 · 静态示意；正式工具输出 PNG", 20, "#8392a7")]
    content += ['<rect x="48" y="204" width="640" height="480" rx="12" fill="#081322" stroke="#304358"/>',
                '<rect x="796" y="145" width="420" height="560" rx="12" fill="#081322" stroke="#304358"/>']
    content += [text(96, 256, "KOSPI · 1990—2026", 18, "#d7bd80"),
                text(96, 342, "韩股36年", 68, weight=700),
                text(96, 388, "2分钟回看股市沉浮", 25, "#b6c3d3")]
    graph, _ = chart(rows, 96, 442, 544, 150, 10000, 1.3)
    content += [graph, text(96, 650, "数据截至 2026.10.16 · 日收盘点位", 15, "#8392a7"),
                text(48, 729, "横版 4:3 / 1440×1080 PNG", 22, "#a7b6c9")]
    content += [text(830, 197, "KOSPI · 1990—2026", 16, "#d7bd80"),
                text(830, 283, "韩股", 69, weight=700), text(830, 365, "36年", 77, "#fff0d1", 700),
                text(830, 413, "2分钟回看股市沉浮", 22, "#b6c3d3")]
    graph, _ = chart(rows, 830, 464, 352, 133, 10000, 1.1)
    content += [graph, text(830, 663, "数据截至 2026.10.16", 14, "#8392a7"),
                text(796, 750, "竖版 3:4 / 1080×1440 PNG", 22, "#a7b6c9"),
                text(48, 812, "曲线保留 2026 年高点之后的回落 · 关键文字距画幅边缘至少 7%", 19, "#8392a7")]
    (OUT / "cover-layouts.svg").write_text(shell(1280, 850, "横竖封面独立版式示意", "\n".join(content)), encoding="utf-8")


if __name__ == "__main__":
    preview("kospi", "韩股36年", "KOSPI / 1990—2026 · 电影感数据叙事", 10000)
    preview("nikkei", "日股40年", "NIKKEI 225 / 1985—2025 · 完整历史静态示意", 60000)
    covers()
