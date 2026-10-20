# 星光开场与事件过渡

## 当前恒生科技版本：恢复日股配乐与可读坐标（2026-10-20）

用户明确偏好日股原配乐及图表可读性。本机使用 `projects/hstech/project-readable.json`：`display.motion_style` 为 `nebula_chart`，`audio.score` 为 `energetic220`。音乐是 140 BPM 的《Forty Years / Momentum》；事件音效按恒生科技的事件配置合成。

恢复日股版的大号报价、清晰价格刻度和年份、独立事件卡以及全历史导航，使用深蓝底和金白主线。保留星尘散开聚拢的开场、走势端点星芒与事件光尘；降低正文装饰强度并裁切在主图内。开场末段 2.35—3 秒逐渐揭示图表。真实行情、累计压缩和所有事件槽位保持不变。

复现：`python tools/stock_video.py build --project projects/hstech/project-readable.json --workers 3 --encoder libx264 --output output/hstech-readable-full`。各旧风格保留为独立选项，以下按历史顺序记录。

## 参考与观察范围

2026-10-20，在 Chrome 中查看用户提供的[抖音视频](https://www.douyin.com/jingxuan?modal_id=7693847416717865147)，页面作者为 @青衣怀袖，标题为《A股35年历史〈群星闪耀时！〉》，播放器总时长约 296.7 秒。观察了开场及若干中段画面，没有完整观看或试听。部分拖动发生缓冲，因此以下时间为大致位置，不能当作逐帧分镜。

- 开头约 2 秒：深色背景、简短问题，让观众先进入叙事。
- 约 4—6 秒：金白曲线逐步绘出，运动端点高亮，局部十字星芒把视线引向曲线。
- 约 7—10 秒：蓝金星点形成空间感，接续文字问题；通过亮点和粒子连接镜头。
- 约 13 秒及中段：从初始点位进入历史图表，配合年份、事件文字、光幕与长期走势小图。

以上是对可见画面的观察。粒子、叠加光晕、路径描边和缓动均能实现类似视觉语言，但不能仅凭成片断言作者使用了 After Effects、Three.js 或某个插件。未下载、使用参考作品的画面、音频、脚本或源素材。

## 当前实现

共享 C# classic220 渲染器新增可选 stellar 风格，通过已有 Skia/GDI 绘图接口完成，无新渲染依赖。

1. 0—3 秒：恒生科技标题与“跌落之后，还能重回巅峰吗？”出现，蓝金光尘围绕中心运动，随后聚拢到首个真实行情节点，叠化进入图表。
2. 正文：低亮度、分层速度的星点背景；已有仙女棒线头、转折圆环和事件提示继续使用。
3. major 事件：真实到达时刻增加柔和节点光晕、横向镜头光条和事件卡边缘扫光，持续约一秒。

这次没有延长原模板开场到参考作品的十余秒，也没有套用其三维倾斜视角。用户要求的累计历史压缩、真实报价及手机安全边距优先保留。星点只是装饰，不表示行情、成交量或未来方向。

## 配置与复现

在 classic220 项目的 display 中加入：

```json
{
  "motion_style": "stellar",
  "intro_question": "跌落之后，还能重回巅峰吗？"
}
```

本机恒生科技独立配置：`projects/hstech/project-stellar.json`。项目和媒体目录依照仓库规则不提交。

```sh
python tools/stock_video.py preview --project projects/hstech/project-stellar.json --start 0 --seconds 4 --encoder libx264 --output output/hstech-stellar-intro-hq
python tools/stock_video.py build --project projects/hstech/project-stellar.json --workers 3 --encoder libx264 --output output/hstech-stellar-full
python tools/stock_video.py covers --project projects/hstech/project-stellar.json --output output/hstech-stellar-covers
```

`output/hstech-stellar-full/highlights-14s.mp4` 与 `before-14s.mp4` 分别为新版和原版的相同摘录：0—6 秒、16—20 秒、96—100 秒。摘录拼接处不是全片转场。全片为 video.mp4，验收范围见 validation.md。

## 行星轨迹修订（2026-10-20）

用户进一步要求“行星散开再聚拢成一点，由点开始走势”，并降低坐标和时间轴的存在感。因此增加独立的 `display.motion_style: "planetary"`，不覆盖 stellar 的表现。

- 开场保留 3 秒：有明暗层次的中心星体与卫星向外散开，1150 个光尘粒子以带深度的旋转路径回聚，最终收束至首个真实行情节点；随后星体沿真实日收盘轨迹运动。
- 正文去除背景网格、坐标框、横轴刻度和底部导航；仅保留很淡的价格参考数值，以及日期和实际收盘报价。主图继续累计展示完整历史，不切掉过去走势。
- 细化主线、去除面积填充及竖直辅助线；节点使用球体表面、柔光、横向光条和轨道尘埃。关键事件及转折的尘埃先扩散再回聚，事件文案采用悬浮淡入，去掉大块卡片和重复图内标签。
- 用户要求移除“腾讯财经”的可见字样。新配置 `display.source` 为空；行星版页脚仅显示 HSTECH 与区间。`data_source` 和 sources.json 保留实际来源及核验记录。

画面配置 `projects/hstech/project-planetary.json`；配新器乐的交付配置 `projects/hstech/project-planetary-rock.json`。球体、粒子与事件动效均按全局时间计算。相同行情状态继续接受累计历史、预览一致性与分段连接检查。

本次新编器乐《星火轨道》为 156 BPM、E minor / G major 的原创流行摇滚，采用本机 AVFoundation 离线 MIDI 乐器渲染，双吉他、贝斯、鼓组、清音主旋律、铺底与钢琴共 9438 个音符事件。0—3 秒空间音色，3 秒进入鼓点；97—113 秒降低密度，115—150 秒与 185—212 秒进入更丰满的段落；218 秒降低音乐为结尾问题留空间。它借鉴热血青春音乐的宽泛编配特征，没有使用《青春之火》的音频、歌词、旋律或歌手声音。

该器乐不是 ACE-Step 人声输出。ACE-Step 在线任务此前失败且配额耗尽；本次建立独立 MLX 环境并固定兼容开发分支，成功导入模型加载器，但权重下载超时，未生成神经网络音乐。两条路径与音源记录分别保存在本机 music-orbit 和 music-youth 目录，不能混称。
# 星云光轨修订与本地人声取消（2026-10-20）

再次通过浏览器观察参考视频的约 6、10、12、17 秒画面，确认主要视觉语言为暗红/蓝紫星云、密集细星尘、横向耀光及细走势线。新增可选 `display.motion_style: "nebula"`：用原创程序噪声生成背景，2400 个确定性星尘散开后收束到第一笔真实报价；开场仍为 3 秒。运动节点改成光核，不再使用实体球体。沿用累计历史、弱坐标和事件提示，不下载或复用参考片素材。

本机配置 `projects/hstech/project-nebula.json` 与输出 `output/hstech-nebula-visual/` 仍使用上一版原创器乐，不能称作 AI 人声版。用户随后取消本地人声方案；模型下载已终止，专用模型、MLX 环境、源码副本和在线客户端环境已删除。保留原创歌词、脚本和日志，未生成模型音频，也没有后台重试。
