# macOS 与跨平台渲染

Mac 使用 Python + .NET 8 + SkiaSharp + FFmpeg/FFprobe，不需要 PowerShell。Windows 的原 GDI+ 后端继续可选。两种后端共享 C# 场景和时间轴；SkiaSharp 适配实际使用的绘图操作，不是通用 System.Drawing 实现。字体、渐变和抗锯齿可能与 Windows 参考帧不同。

## 安装与运行

安装 Python 3.11+、.NET 8 SDK 和包含 libx264、AAC、loudnorm、FFprobe 的 FFmpeg，将可执行文件加入 PATH。Apple Silicon 使用 arm64 SDK。也可通过 DOTNET_BIN、FFMPEG_BIN、FFPROBE_BIN 指定程序。

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python tools/stock_video.py doctor
.venv/bin/python tools/stock_video.py init --preset cinematic120 --output projects/my_video
.venv/bin/python tools/stock_video.py preview --project projects/my_video/project.json --seconds 2 --scale 0.5 --encoder libx264 --output output/mac-preview
.venv/bin/python tools/stock_video.py covers --project projects/my_video/project.json --output output/mac-covers
.venv/bin/python tools/stock_video.py build --project projects/my_video/project.json --encoder libx264 --output output/mac-full
.venv/bin/python tools/stock_video.py verify --project projects/my_video/project.json --output output/mac-full
```

首次运行自动构建 engines/portable，NuGet 需要联网获取依赖；后续源码不变时复用构建。也可以手工运行：

```bash
dotnet build engines/portable/StockVideo.Renderer.csproj -c Release
python3 -m unittest discover -s tests -v
```

本次开发使用的隔离 SDK 安装目录为 `~/.local/share/stock-video/dotnet`，工具在 PATH 没有 dotnet 时也会检查该目录。正常安装 .NET 的用户不需要这个目录。

## 后端与预览

`--backend auto` 在 Mac 选择 skia、Windows 选择 windows；可以显式指定。Mac 不支持 windows 后端。

`preview --scale 0.25`、`0.5` 或 `1` 控制输出分辨率，默认 1。低清模式实际降低画布像素，电影感预览同时关闭两倍超采样；时间轴、数据和事件不变。build 不接受 scale，正式视频保持原预设规格。低清片不代替正式尺寸排版验收。

例如日股：

```bash
.venv/bin/python tools/stock_video.py preview --project examples/nikkei/project.json --start 29.6 --seconds 2 --scale 0.5 --output output/nikkei-preview
```

## 字体

Mac 默认使用系统 PingFang SC 中文和 Helvetica Neue 数字。配置可增加：

```json
"fonts": {
  "text_family": "PingFang SC",
  "number_family": "Helvetica Neue"
}
```

也可指定 `text_file` / `number_file`，相对项目配置目录解析，文件优先于 family；使用字体文件时粗细由所选文件决定。自行确认字体许可，仓库不分发系统字体。上述设置仅用于 skia，旧 Windows 后端仍沿用原字体。

## 分段续跑

默认复用完成的分段。缓存同时匹配配置、CSV、代码、后端构建、运行时、编码器、FFmpeg 版本、字体文件、分辨率和帧范围，并校验视频与逐帧记录的 SHA-256。中断或损坏分段会重做。

用 `--no-resume` 强制重新渲染。输出目录不要同时用于两个运行中的构建；不同项目使用独立目录。完整成片仍需重新拼接、解码和验收，缓存命中不等于跳过最终检查。

## 当前范围

本次保留两套原有时长、画幅和分镜槽位，没有提供任意画幅、任意时长、图形化编辑器或 Remotion 后端。Mac 先使用 CPU libx264；未实现 VideoToolbox 加速。

Python 验收改用 FFprobe JSON 读取媒体信息，FFmpeg 仍完整解码并检查帧数。事件停留检查同时覆盖预览区间与完整视频的预期帧数。跨平台本次实际验证范围见 validation.md 的 Mac 记录；原 Windows 完整验收不能替代本次检查。

## 日经上传母版质量

classic220 的 CPU libx264 编码使用 CRF 14、slow（原为 CRF 18、fast），从原始渲染帧编码以减少细线、文字与粒子的有损压缩。分辨率仍为 1080×1920、30 fps；这不是对旧 MP4 提高码率或放大。NVENC 参数不变。文件大小取决于画面复杂度，不设置固定体积目标。使用独立输出目录，例如 `output/nikkei-hq-full`，保留前版用于比较。
