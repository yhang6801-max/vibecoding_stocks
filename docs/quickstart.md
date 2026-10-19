# 从下载到第一支视频

## 1. 安装与检查

首版面向 Windows，使用 PowerShell 7、Python 3.11+ 和 FFmpeg。下载 [PowerShell](https://github.com/PowerShell/PowerShell/releases)、[Python](https://www.python.org/downloads/windows/) 与适合 Windows 的 [FFmpeg 构建](https://ffmpeg.org/download.html#build-windows)，将可执行文件加入 PATH。FFmpeg 构建至少需要 H.264 `libx264`、AAC 和 `loudnorm`。安装了 NVIDIA 驱动并不代表 NVENC 一定可用，工具会实际编码探测。

在仓库根目录运行：

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe tools/stock_video.py doctor
```

使用虚拟环境 Python 的完整路径，无需执行激活脚本。`doctor` 检查依赖、字体、PowerShell、FFmpeg 与编码器，不会制作完整视频。FFmpeg 位于其他路径时：

```powershell
$env:FFMPEG_BIN = 'C:\Tools\ffmpeg\bin\ffmpeg.exe'
.\.venv\Scripts\python.exe tools/stock_video.py doctor
```

工具按显式 `--ffmpeg`、`FFMPEG_BIN`、PATH 的顺序查找。当前媒体检查直接调用 FFmpeg，不要求另装 `ffprobe`。

可先运行 8 项输入契约测试，不渲染视频：

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

已完成的独立目录复现与第三标的替换范围见[验收记录](validation.md)。

## 2. 先运行原样示例

```powershell
.\.venv\Scripts\python.exe tools/stock_video.py init --preset cinematic120 --output projects/kospi_copy
.\.venv\Scripts\python.exe tools/stock_video.py preview --project projects/kospi_copy/project.json --seconds 5 --encoder libx264
.\.venv\Scripts\python.exe tools/stock_video.py covers --project projects/kospi_copy/project.json
```

首次建议用 CPU 短预览确认整条链路。预览只包含开头指定秒数，不能作为全历史完整版发布。预设初始化会复制实际数据、事件与文案；它不会联网下载最新报价，也不会把假行情写入项目。

完整韩股示例：

```powershell
.\.venv\Scripts\python.exe tools/stock_video.py build --project projects/kospi_copy/project.json --workers 3 --encoder auto
.\.venv\Scripts\python.exe tools/stock_video.py verify --project projects/kospi_copy/project.json
```

日股示例：

```powershell
.\.venv\Scripts\python.exe tools/stock_video.py init --preset classic220 --output projects/nikkei_copy
.\.venv\Scripts\python.exe tools/stock_video.py preview --project projects/nikkei_copy/project.json --seconds 5 --encoder libx264
.\.venv\Scripts\python.exe tools/stock_video.py build --project projects/nikkei_copy/project.json --workers 3
.\.venv\Scripts\python.exe tools/stock_video.py verify --project projects/nikkei_copy/project.json
```

`classic220` 输出静音片；韩股原创配乐属于 `cinematic120` 链路。日股旧体验音轨未收入仓库。

需要查看中后段时，可为 `preview` 增加 `--start 70 --seconds 5`，时间单位为秒，范围须在预设总时长内。电影感短预览也会首次合成完整配乐，再截取对应片段，因此第一次运行会比后续预览慢。预览与全片默认使用同一输出目录，最后一次执行的 `verify` 检查最近生成的媒体记录；需要同时保留两份记录时，为预览另传 `--output projects/kospi_copy/preview_output`，验收时使用相同目录。

## 3. 查看和发布

默认输出位于所选项目的 `output/`，可通过渲染命令的 `--output` 更换目录。查看成片、关键帧、数据与技术验收记录，以及生成的本地审阅页面。移动项目时保留文件间的相对位置。

封面命令生成两张 PNG：横封面 1440×1080（4:3）、竖封面 1080×1440（3:4），并导出发布文案。封面画幅独立于成片画幅，二者均需检查。

先看首尾、区间最低点、最高点、高点后的回落、密集标签和镜头变化，再完整播放。韩股有声版另外试听整段覆盖、提示音和首尾；技术参数通过不能代替主观观看和试听。

需要自己的视频时，按[新项目指南](new-project.md)修改复制出的项目。不要直接编辑 `examples/` 里的基准样例。
