# 日经竖屏动效改版

2026-10-19 调研与实现。仍使用 classic220 的 220 秒、30 fps、1080×1920、17 个事件和最后两秒全景。此版为视觉升级，保持静音。CSV、事件时间、真实报价和原有平滑跟随视窗不变。

## 参考与取舍

- [VideoCaptions 的动态图表设计](https://www.videocaptions.ai/motion-elements/animated-bar-chart-video)：逐步绘制、柔和面积、突出一个重点数字、减少手机画面网格密度。借鉴信息层级，不使用其少量手填数据替代完整日线。
- [Remotion 动画数学](https://www.remotion.dev/docs/animation-math)：由帧时间组合入场和退场状态。本项目用已有 C# 全局时间实现缓动，不引入 React 运行时。
- [Remotion 官方 Skills](https://github.com/remotion-dev/skills)：可编程视频的动画与制作指南。此次仅调研，未安装依赖或复制实现。
- [Adobe 镜头动画教程](https://www.adobe.com/learn/animate/web/animate-camera-tool)：镜头构图、平移、缩放与时间轴组织。当前保留既有连续视窗，避免为炫技倾斜坐标或扭曲价格幅度。

以上参考是技术文档与案例页面调研，不声称完整观看示例视频。

## 已落地

- 深蓝黑底色，弱化背景网格；纵轴辅助线和季度月份优先。
- 金白曲线采用四层透明笔触、柔和面积与游标光晕，真实曲线坐标不变。
- 重排顶部标题、精确到小数点后两位的实际收盘和完整交易日期。
- 三秒标题缓动入场，事件出现时扩散圆环；下方独立事件卡片帮助手机阅读。
- 底部全量日线小图标明“含后续走势”，以免将导航误认为当前已发生行情。
- 全景显示首尾年份、全期最低与最高值，保留最后真实交易日。

实现位于 engines/classic_renderer.cs；Windows 与 Mac 共用。当前已运行的后端为 Mac Skia，Windows 视觉尚未复验。

## 复现

```bash
.venv/bin/python tools/stock_video.py preview --project examples/nikkei/project.json --start 204 --seconds 6 --scale 1 --output output/nikkei-v2-breakout
.venv/bin/python tools/stock_video.py build --project examples/nikkei/project.json --workers 3 --encoder libx264 --output output/nikkei-v2-full
```

旧成片 output/mac-nikkei-full/video.mp4 保留，可直接对比。短预览还覆盖开头、1989 年峰值和片尾；详细验收见 validation.md。

## 累计压缩修订

用户要求主图始终保留之前的走势，因此当前版改为固定起始月份、随时间扩大横轴范围。起初显示一年宽度，之后当前时间约位于横轴 94% 处，旧行情逐渐横向压缩。纵轴统计起始日至当前已发生行情的全部极值。刻度按跨度切换为季度、1/2/5/10 年间隔；最后两秒继续相同主图并显示结束文案，不再突切另一套坐标。

完整真实日线仍参与计算。为控制长历史过度绘制，每个逻辑像素列按时间顺序保留首、尾、最低、最高点；不会用稀疏抽样丢掉尖峰。主图连接真实日收盘；它不是 OHLC 蜡烛图。事件文字只在到达附近展示，以免旧事件堆积遮挡完整走势。底部导航亮区改为起始日至当前日期。

完整成片：output/nikkei-cumulative-full/video.mp4。实际产物检查：

```bash
.venv/bin/python tests/check_cumulative_render.py output/nikkei-cumulative-full
```

该检查遍历 6600 帧，检查起点固定、跨度不减、累计极值不被纵轴裁掉、真实报价及最终交易日。

## 仙女棒端点预览

classic_renderer.cs 新增 DrawSparklers：每秒 120 次发射、0.45—1.15 秒寿命，金白亮核、金橙飞散火星、短拖尾与重力下落；事件到达后的 0.28 秒增大发射量。粒子按出生时的真实交易样本锚定，随累计坐标重新投影，不跟随当前端点整体平移。随机参数由粒子 ID 与固定种子确定，使用全局视频秒数，不依赖分段起点。粒子被裁剪在主图区，文字绘制在粒子之后。

当前只制作 202—214 秒的 12 秒预览，未重新制作整片。运行：

```bash
.venv/bin/python tools/stock_video.py preview --project examples/nikkei/project.json --start 202 --seconds 12 --scale 1 --encoder libx264 --output output/nikkei-sparkler-preview
```

## 关键事件与转折强调预览

新增关键事件三层扩散环、72 条放射火星、低透明度画面染色和大标题卡片，保留原来的仙女棒。阶段反弹/回落分别用金色/青色提示，44 条放射火星。事件优先于同时发生的转折，提示退出时淡出；主曲线和报价不缩放、不抖动。

转折提示使用因果的 8% 方向变化规则：上涨阶段跟踪此前最高收盘，回落达到 8% 时确认；下跌阶段跟踪此前最低收盘，反弹达到 8% 时确认。首次方向建立不提示。确认显示在阈值跨越当天，卡片保留确认日期和当时幅度，不回填到过去的高低点。转折提示冷却 3 秒，关键事件到达后三秒优先展示事件，215 秒后不新增转折。该规则用于视频提示，并非对每个局部极值的定义。

当前只制作 203—216 秒、13 秒原尺寸预览（output/nikkei-impact-preview/preview.mp4），仍为静音，未重制全片。各分段保存 impact_cues.json。实际检查：

```bash
.venv/bin/python tests/check_impact_render.py output/nikkei-impact-preview output/nikkei-cumulative-full
```
