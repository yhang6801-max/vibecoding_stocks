# 第三方材料与来源

MIT 适用于本仓库原创代码、文档、视觉源码和原创数学合成配乐源码。以下材料按各自权利与使用条件处理；保留来源不等于取得了新的数据再分发许可。

## 行情快照

| 示例 | 内容与口径 | 来源 |
| --- | --- | --- |
| 日经225 | 1985-01-04—2025-10-31，10,021 条日收盘点位；价格指数，不含分红再投资 | [FRED NIKKEI225](https://fred.stlouisfed.org/series/NIKKEI225)，原提供方 Nikkei Inc.；[FRED 使用条款](https://fred.stlouisfed.org/legal/terms-of-use)与 [Nikkei 指数许可](https://indexes.nikkei.co.jp/en/nkave/index/licensing) |
| 韩国 KOSPI | 1990-01-04—2026-10-16，9,489 条日收盘点位；价格指数，不含分红再投资 | [Naver Finance](https://finance.naver.com/sise/sise_index.naver?code=KOSPI)、[FinanceDataReader KRX 缓存](https://github.com/FinanceData/fdr_krx_data_cache/tree/1000fb25a27e6b0e8d899048ac392da22139c996/data/index/year_ks11)、[KRX](https://global.krx.co.kr/) |

CSV 是制作时留存的复现快照，不是实时行情服务。具体源地址与核对范围另见各示例的 `sources.json`。韩股 7,924 条共同记录与固定缓存交叉核对，另 1,565 条仅有 Naver 来源；不把全部数据宣称为双来源核验。日股非交易日与缺失值处理继承原项目，换数据时需重新检查。

韩股主源原记录的下载时间为 2026-10-18 09:40:37 UTC，次源32份年度缓存抓取于同日 12:17—12:20 UTC。日股原工程保留了 FRED 来源标记和 2026-10-17 复盘，但未保存可追溯的网络下载时间；`sources.json` 对该时间记为 `null`，不使用文件修改时间推断。两份 `daily.csv` 与原工程留存文件逐字节一致，当前 SHA-256、样本数和极值记录在各自的来源文件中。

第三方数据、指数名称与商标不在本仓库 MIT 许可范围内。用于外部再分发或商业发布时，按原提供方条款处理。示例快照的保留是为了说明和复现项目；本项目不代表数据提供方授权。

## 字体与运行环境

- 微软雅黑、Bahnschrift 由使用者的 Windows 系统提供，仓库不包含字体文件。字体适用 Windows 及字体自身的许可条款。
- [PowerShell](https://github.com/PowerShell/PowerShell)、[Python](https://www.python.org/)、[NumPy](https://numpy.org/)、[FFmpeg](https://ffmpeg.org/legal.html) 作为外部依赖安装，不在此重新授权；FFmpeg 的实际许可取决于构建选项。
- .NET `System.Drawing` 使用 Windows 图形环境，首版不承诺其他系统运行。

## 配乐、参考与其他素材

韩股配乐与事件音效由仓库数学合成源码生成，不使用外部录音、采样包或他人歌曲。生成音频可按原创源码的 MIT 条件使用，仓库不附大体积 WAV。

日股旧体验版的音轨缺少原始素材、授权记录和混音源码，因此不收入仓库；`classic220` 示例保留静音复现。第三方录屏、调研源码快照和平台界面截图均不分发。版式示意 SVG 根据本仓库示例 CSV 自行绘制；它们不是成片验收截图。

历史背景引用：

- [IMF：1997 年韩国备用信贷安排](https://www.imf.org/en/News/Articles/2015/09/14/01/49/pr9755)
- [BIS：金融危机时间与背景资料](https://www.bis.org/publications/ii-3)
- [WHO：COVID-19 时间线](https://www.who.int/news/item/27-04-2020-who-timeline---covid-19)

引用用于核对背景事实；正文不据此自动认定某事件是某次价格变化的唯一原因。
