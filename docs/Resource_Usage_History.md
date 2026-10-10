# Desktop Hub — 各版本资源占用记录

本文件按开发版本追加实测结果，保留原始证据。M1 是首个 Windows 客户端基线；后续版本采用相同环境、设置和采样方法后，再计算变化。本次把 **Mini 作为主要常驻模式**，同时测量 Dock、Workspace 和使用后返回 Mini 的情况。

## 记录口径

- 主指标是 **私有提交内存**：宿主 `PrivateMemorySize64` 加上属于本应用的全部 WebView2 进程，除以 1,048,576 得到 MiB。它不是精确物理 RAM 占用，也不是只看 EXE 的内存。
- 通过 `Environment.GetProcessInfos()` 取得 WebView2 进程，并补充这些进程的 `msedgewebview2.exe` 后代（包括 API 未返回的 crashpad）。PID 去重，CPU 基线同时验证 PID 和启动时间，排除其他应用的 WebView2。
- CPU 整机百分比 = `100 × 所有归属进程的 CPU 秒数增量 ÷ 实际间隔秒数 ÷ 逻辑处理器数`。另外保留单核等价百分比；本机 16 个逻辑处理器，一个核心满载约为整机 6.25%。
- 每态稳定 15 秒，再按约 1 秒间隔测量 60 秒；保存 61 帧（基线 + 60 个统计样本），内存均值/峰值使用后 60 帧。CPU 按有效区间的实际时长加权，不把进程更替时无法完整统计的区间填成零。
- 峰值是同一帧的总私有内存最大值。工作集求和仅作辅助观察，共享页会重复计数，不能当作精确物理内存总量。
- 测量包含诊断采样自身的宿主开销。采样期间不截图、不启动第三方程序、不打开 DevTools、不强制 GC 或清空工作集；不包含 GPU 显存/利用率、磁盘 IO、网络、冷启动耗时或长期泄漏验证。

依据：[WebView2 进程模型](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model)、[Windows 工作集](https://learn.microsoft.com/en-us/windows/win32/memory/working-set)。

## 版本对比

| 版本 | 日期 | 主要模式 | 首次 Mini 私有内存均值 | 用过其他窗口后 Mini 均值 | 首次 Mini CPU 均值 | 较前版变化 |
| --- | --- | --- | ---: | ---: | ---: | --- |
| M1 / 0.1.0 | 2026-10-09 | Mini | 373.8 MiB | 686.0 MiB | 0.428% | 首次基线 |

以后追加版本，分别比较首次 Mini 和使用后 Mini：`ΔMiB = 新版均值 − 前版均值`；`Δ% = 100 × ΔMiB ÷ 前版均值`。正数表示增加、负数表示减少。硬件、DPI、运行时、设置或采样情境变化时标为“条件不同，不能直接比较”，保留旧行，不覆盖历史证据。M0 是浏览器原型，不与原生 M1 混算。

## M1 实测详情

本机普通用户启动发布 EXE 成功，原生握手与 Mini 尺寸上报完成。随后运行同一 EXE 的显式 `--resource-test` 入口；它使用隔离的 `resource-data` 配置和 WebView2 profile，直接切换真正的 WPF 窗口，并逐帧验证 HWND 与可见性。普通用户配置不作为诊断写入目标。诊断不模拟窗口、不启动本地服务器。

### 环境与固定设置

| 项目 | 本次值 |
| --- | --- |
| 系统 | Windows 11 家庭版中文版，build 26200，x64 |
| CPU | Intel Core Ultra 7 255H；16 核 / 16 逻辑处理器 |
| 系统物理内存 | 33,752,997,888 字节（约 31.4 GiB 可报告内存） |
| 显示 | 工作区 1600 × 952 DIP，200% DPI |
| .NET 运行时 | 本机已安装 .NET 8.0.17，未强制使用项目内 SDK 运行时 |
| WebView2 | Runtime 154.0.4258.62；SDK 1.0.4258.31 |
| 主题/动画/透明 | 深色 / 动画开启 / 透明开启 |
| 悬停 | 诊断固定关闭边缘悬停，避免测量时鼠标意外展开 Dock |
| 内容 | 无已选择程序；Workspace 首页；原型默认演示内容 |
| 顺序 | 首次 Mini → Dock 展开 → Workspace 首页 → 关闭后返回 Mini |
| 当前发布文件大小 | 26,880,986 字节，约 25.6 MiB；不含 .NET/WebView2 Runtime、测试 profile 和采样证据 |

这里记录的是固定设置下的空闲基线。普通的系统主题、悬停开启、其他页面或用户操作可能产生不同开销，不能把本表当作所有实际使用条件的保证。

### 各模式结果

内存列均为私有提交内存，CPU 为整机归一化百分比。Mini 在其他模式中仍显示，表中的开销是该使用情境下的整个 Desktop Hub 进程组，不能拆成单窗口独占内存。

| 情境 | 宿主均值 MiB | WebView2 均值 MiB | 合计均值 MiB | 合计峰值 MiB | CPU 均值 | CPU 观测峰值 | 有效 CPU 区间 | 进程数 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| 首次 Mini 常驻 | 159.6 | 214.2 | 373.8 | 376.1 | 0.428% | 1.760% | 60/60 | 7 |
| Dock 展开 | 213.2 | 311.0 | 524.2 | 991.8 | 0.357% | 0.875% | 58/60 | 8–9 |
| Workspace 首页 | 274.7 | 412.6 | 687.3 | 701.7 | 0.363% | 0.781% | 60/60 | 9 |
| 使用后返回 Mini | 275.6 | 410.3 | 686.0 | 686.4 | 0.410% | 0.878% | 60/60 | 9 |

Dock 的第 30 个统计样本同时出现两代 `Gpu` 进程：旧 PID 37312 私有内存约 475.4 MiB，新 PID 5292 约 150.8 MiB；后一帧旧 PID 消失。该次重叠形成 991.8 MiB 观测峰值，证据保留。第 30/31 区间的 CPU 无法完整计算，因此 Dock 的 CPU 均值/峰值仅覆盖 58 个有效区间，不能据此描述更替瞬间的 CPU 峰值。这里可以确认进程更替，尚未确定驱动或运行时原因。

### 主要结论与证据

首次 Mini 的私有提交内存约 374 MiB，M1 尚不能宣称轻量化资源目标达标。使用 Dock 和 Workspace 后，返回 Mini 的平均值约 686 MiB，比首次 Mini 增加 **312.2 MiB（83.5%）**。这属于同版本不同使用状态的差异，不是跨版本退化百分比，也不足以证明持续泄漏。

返回 Mini 时 Dock/Workspace 都不可见，`IsSuspended=true`，但全部窗口实例和 9 个进程仍在。工作集求和均值从首次 Mini 的约 689.3 MiB，变化为 Dock 885.7 MiB、Workspace 1036.8 MiB、返回 Mini 972.8 MiB；这组数含共享页重复，不能解释为精确物理总内存。挂起降低活动，不等于释放所有图形资源或缓存。本轮未实施资源优化或 M2 功能。

本次完整运行于 **2026-10-09 21:01:41–21:06:43（Asia/Shanghai）**，4 个真实窗口使用情境完成；原始 244 帧、240 个计划 CPU 区间中 238 个有效、2 个因 GPU 进程更替跳过。每态 60 个内存统计样本完整，无进程读取错误。独立从原始帧重新计算内存均值/峰值和加权 CPU，与诊断汇总相符。

完成后于 21:11 再通过根目录 `打开 Desktop Hub.lnk` 启动普通实例，成功读取原有配置并完成 `app.getState` / Mini 尺寸握手。诊断前后的用户设置 SHA256 一致；[快捷方式启动验证](resource-usage/M1/run-20261009T130140953Z/shortcut-launch.json)已保存，普通应用保持运行。

证据保存在 [本次运行目录](resource-usage/M1/run-20261009T130140953Z/)：

- [汇总 JSON](resource-usage/M1/run-20261009T130140953Z/summary.json)、[汇总 CSV](resource-usage/M1/run-20261009T130140953Z/summary.csv)
- [逐帧 JSON](resource-usage/M1/run-20261009T130140953Z/samples.json)、[逐帧 CSV](resource-usage/M1/run-20261009T130140953Z/samples.csv)
- [独立计算复核](resource-usage/M1/run-20261009T130140953Z/analysis.json)、[运行时/构建/设置](resource-usage/M1/run-20261009T130140953Z/environment.json)、[硬件](resource-usage/M1/run-20261009T130140953Z/hardware.json)、[宿主日志](resource-usage/M1/run-20261009T130140953Z/host.log)

采样时 `DesktopHub.Windows.dll` SHA256 为 `92206050A52949C5CA3C27BFBBAD88B2B106C8027CA26F76FE414B38FC441E6A`，完整 EXE/Core 哈希见环境 JSON。采样后的修改只修正诊断文本中的有效 CPU 区间计数，普通窗口逻辑和统计公式未改变。原始 `summary.txt` 曾把 240 个计划区间统一写为 CPU 区间；以汇总 JSON 和独立复核中的 **238 有效 / 2 跳过** 为准，未来运行的文本已修正。

已存的 `docs/m1-evidence/native/*-memory.json` 是早期截图集成测试的即时样本，不能与本次 60 秒空闲基线直接计算优化百分比。

### 复测与追加

在空闲桌面运行，诊断会短暂显示 Dock/Workspace，完成后退出诊断实例：

```powershell
# 已有当前发布目录
.\scripts\measure-resources.ps1 -SkipBuild

# 先离线构建、测试并发布，再测量
.\scripts\measure-resources.ps1
```

每次采样归档到 `docs/resource-usage/M1/run-<UTC时间>/`。原始 JSON 包括每帧进程内存、累计 CPU、时间和错误；汇总 JSON 包括模式、实际采样时长、跳过区间与 HWND/挂起状态。后续版本应保留自己的版本目录，追加本文件的版本对比行和模式详情，并写明构建 SHA256 和环境变化。
