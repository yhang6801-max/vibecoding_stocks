# 日经完整版配乐

2026-10-19 调研与制作。检索到了 [CapCut Phonk 剪辑模板](https://www.capcut.com/template-detail/Phonk-Edit/7116458278682397958)、[Phonk 音乐可视化 NEON BLADE 案例](https://www.bilibili.com/video/BV1sC41147FW/)、[Pixabay Cinematic Phonk 风格曲目](https://pixabay.com/music/beats-cinematic-phonk-223316/)。这些支持将 Phonk/强鼓点电子乐作为风格参考，不能证明具体曲目是 K 线视频使用量最高的 BGM。未下载、采样或使用这些曲目的录音与旋律。

本片使用新创作的程序化配乐 **Forty Years / Momentum**：140 BPM，D 小调中心的电子律动、四拍底鼓、低音、琶音、金属打击音色；中段收敛，后段增强。所有乐器由 engines/energetic_audio.py 与 audio_instruments.py 合成。17 个配置事件使用同一事件表添加上升音效和重击；自动识别的阶段转折仍仅有视觉提示。218 秒收掉节拍主体，为结尾问题留空间。

这是原创 Phonk 风格打击乐 / 电子配乐，不是某首热门歌曲的复刻，也未使用外部音乐生成服务。编排源码及原创合成配乐沿用仓库 MIT 授权；行情、字体不受此声明影响。

## 运行

```bash
.venv/bin/python tools/stock_video.py build --project examples/nikkei/project-energetic.json --workers 3 --encoder libx264 --output output/nikkei-energetic-full
```

project-energetic.json 与静音版使用同一份 daily.csv、同一事件时间及 220 秒规格，通过 audio.score=energetic220 显式启用配乐。原 project.json 保持静音。验收器按配置判断是否应有声音，并检查完整 AAC 解码、48 kHz 双声道、综合响度及真峰值。

音频输出保留 music.wav、sfx.wav、mix_raw.wav、mix.wav、manifest.json 和两遍响度处理日志。目标 −16 LUFS，母带真峰值上限 −1.8 dBTP，为 AAC 编码留余量。技术验证不代替主观试听。
