# Desktop Hub — M1 实施报告

日期：2026-10-09（Asia/Shanghai）　版本：0.1.0　范围：本轮用户明确限定的 M1 A–E。

本轮已建立真实 Windows 客户端，保留已验收的 HTML 原型，接入独立窗口、受限原生通信、单程序启动和本地设置。完整场景调度、文件索引、脚本及其他 M2 功能未实施。

## 1. 项目检查与实现原则

检查并阅读了 `PRD.md`、`docs/Desktop_Hub_UI_Spec_v0.7.md`、`prototype/` 的 HTML/CSS/JavaScript、原型验收记录和测试。本轮任务优先于文档中仅适用于 M0 的禁止原生集成条款；其余视觉、文件保护和离线约束继续遵守。目录不是 Git 仓库，无现有 .NET 项目、SDK、Visual Studio 或 NuGet 缓存。

UI/UX 检查采用最小改动原则：用户从 Mini / Dock 进入工作空间，选择程序，启动并读取真实结果；失败可重新选择。产品保留 Quiet Desktop 方向、原有 token、字体、图标、六模块结构及交互。桌面客户端去除模拟桌面与演示控制条，只调整内容在真实窗口内的坐标，并在场景中心增加沿用原样式的“本地程序”区域。未重新设计界面。Windows 专用产品不增加移动端方案。

`prototype/` 源文件未改动；编译输出中的六个主要原型文件与源文件 SHA256 完全一致。桌面适配全部位于 `desktop/`，原型仍可按原方法独立运行。

## 2. 技术选择

采用 C# + WPF + WebView2，`net8.0-windows10.0.17763.0`，x64。Mini / Dock 使用 `WebView2CompositionControl` 与透明 WPF Window；Workspace 使用标准 WebView2 与不透明 WPF Window，避免大面积图形捕获开销。三个窗口分别拥有独立 HWND、尺寸和显示控制，同一应用进程共享 WebView2 Environment 和原生设置服务。Dock 和 Workspace 首次使用时才初始化；隐藏时尝试 `TrySuspendAsync`，显示时恢复并同步最新设置。本机日志确认 Workspace/Dock 挂起返回 True。

| 方案 | HTML 复用与窗口能力 | 系统接口与兼容性 | 依赖与本轮结论 |
| --- | --- | --- | --- |
| WPF + WebView2 | 原 HTML/CSS/JS 直接复用；成熟 HWND 控制；合成控件可进入 WPF 透明视觉树 | C# + 公开 Win32 API；目标 Windows 10 1809+，本机 Windows 11 验证 | .NET Desktop Runtime + WebView2；本轮采用 |
| WinUI 3 + WebView2 | 同样可复用 HTML；适合现代原生界面，窗口特效仍需单独验证 | Windows App SDK，Windows 10 1809+ | 增加 Windows App SDK 部署和窗口适配工作，本轮未发现需要迁移的理由 |
| Tauri | 同样可复用 HTML，多窗口与原生命令桥可扩展 | Rust + Win32；Windows 渲染仍依赖 WebView2 | 开发需要 Rust/C++ 工具链；未实际构建，不声称其资源占用优于本实现 |

Microsoft 的[WPF WebView2 说明](https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/wpf)、[合成控件说明](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2compositioncontrol)、[WinUI 3 平台说明](https://learn.microsoft.com/en-us/windows/apps/winui)、[Tauri 开发前提](https://v2.tauri.app/start/prerequisites/)支持上述依赖和兼容范围。合成控件使用图形捕获，可能比标准 WebView2 帧率低，透明效果不能等同于原生 Acrylic 桌面模糊。

选择 .NET 8 是为了复用当前机器的桌面运行时完成 M1；它不是长期版本锁定。按[微软支持公告](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)，.NET 8/9 于 2026-11-10 结束支持，进入正式分发前应优先迁移 .NET 10 LTS。

## 3. 实际目录结构

```text
E:\桌软工作台\
├─ README.md / PRD.md
├─ prototype/                    已验收原型，完整保留
├─ desktop/
│  ├─ index.html                 桌面入口，引用原型资源
│  ├─ desktop.css                真实窗口坐标与少量启动区域样式
│  ├─ native.js                  v1 WebView2 通信客户端
│  ├─ desktop.js                 原型到原生窗口的适配
│  └─ tests/                     桥接与适配行为测试
├─ src/
│  ├─ DesktopHub.Core/            模型、配置校验/存储、程序注册/启动
│  └─ DesktopHub.Windows/         WPF 宿主、窗口、桥接、Win32、原生验收
├─ tests/
│  ├─ DesktopHub.Core.Tests/       无第三方测试框架的核心测试
│  └─ DesktopHub.LaunchFixture/    只供测试的受控 Windows 程序
├─ scripts/
│  ├─ bootstrap.ps1              官方依赖下载与 SHA512 校验
│  ├─ build.ps1                  离线还原、Release 编译与测试
│  ├─ run.ps1                    启动独立 EXE
│  ├─ publish.ps1                发布 win-x64 运行目录
│  ├─ test-native.ps1            原生链路验收和证据归档
│  └─ toolchain-environment.ps1   构建期间环境变量隔离
├─ docs/
│  ├─ Desktop_Hub_UI_Spec_v0.7.md
│  ├─ M1_Implementation_Report.md
│  ├─ M1_Bridge_Protocol.md
│  └─ m1-evidence/                工具链、测试记录和真实窗口截图
├─ artifacts/win-x64/            发布后的独立启动目录
└─ .tools/                       项目内 SDK、官方包缓存，忽略分发
```

## 4. 已实现功能与边界

**程序宿主：** 本地虚拟 HTTPS origin `desktop-hub.local` 映射到输出内 `UI/` 文件夹，这是 WebView2 内部文件映射，不启动 HTTP 服务、不执行 DNS 网页加载。CSP 禁止联网请求；宿主阻止外部导航、资源请求、弹窗、下载与网页权限。`asInvoker`，不请求管理员权限；进程互斥避免重复实例。Workspace 关闭不退出应用，右上角退出完整关闭所有窗口。

**Mini：** 真实细长透明窗口，默认无任务栏项、不会长期置顶，点击不先抢走前台应用；有原型固定场景、工作空间、Dock 和已选择程序入口。保存位置与尺寸。

**Dock：** 独立窗口，真实显示/隐藏；展开期间置顶，手动收起后撤销置顶并隐藏。支持四边位置、尺寸与悬停偏好；100 ms 计时器读取公开鼠标位置接口，默认约 500 ms 边缘停留后展开。采用覆盖显示器的窗口几何规则保守屏蔽全屏前台窗口。它不证明能够识别所有游戏、排除名单或多显示器条件。

**Workspace：** 独立近全屏窗口，保留任务栏项、支持基础拖动/尺寸控制；首次使用主显示器工作区减去边距，随后使用已保存边界并限制在工作区内。Esc 优先关闭页面弹窗，否则隐藏 Workspace。保存前台 HWND 与进程 ID；窗口有效时尝试 `SetForegroundWindow` 恢复，Windows 拒绝激活时记录实际结果，不强行附加输入线程、不模拟恢复成功。用户已切换到其他外部程序时不抢回焦点。

**通信：** `version/id/type/payload` 请求，关联响应与标准错误；限制来源、消息长度、深度、字段、版本、窗口权限和并发数量。设置更新白名单不接受程序路径或窗口边界；后者由原生窗口管理。任意 command / shell API 不存在。详见 [M1_Bridge_Protocol.md](M1_Bridge_Protocol.md)。

**真实启动：** 场景中心选择原生文件选择器中的单个本地 `.exe`，登记 ID 并保存；按钮通过该 ID 请求 C# `Process.Start`。禁止脚本、快捷方式、UNC/网络目标、命令字符串、参数、已知命令/脚本执行宿主及提权。校验 PE 文件头，返回真实创建进程 PID 或真实 Windows/文件错误。成功含义是 Windows 已创建进程，不保证第三方软件完成初始化。所选 EXE 本身的行为仍由用户选择负责，文件名拦截不构成恶意软件沙盒。

**设置：** `%LOCALAPPDATA%\DesktopHub\settings.json`，schemaVersion=1。保存主题、动效/透明偏好、Mini/Dock 位置与边界、Workspace 边界、悬停偏好和测试程序目标。临时文件写入、flush 后同目录原子替换；损坏/超限/未知版本安全回退，尽可能保留 `settings.corrupt-*.json` 并显示提示。无完整文件索引数据库。WebView2 缓存与少量原型演示数据使用同目录 `WebView2/`，不作为真实业务数据源。

**明确未实现：** 完整场景执行、脚本、文件扫描/监测/修改、桌面图标控制、代理、开机自启、云端、登录、完整索引库、真实全局快捷键、托盘、WorkerW 壁纸嵌入与完整游戏/排除名单识别。原型页面保留演示标识；桌面端不会用原型模拟场景的成功反馈代替真实程序启动。未推进 M2。

## 5. 编译与实际验收

验证环境：Windows 11 25H2，build 26200.9457，x64；本机主显示器工作区 1600 × 952 WPF 逻辑像素、截图 DPI 200%。项目内 .NET SDK 8.0.425 / Desktop Runtime 8.0.31，WebView2 Runtime 154.0.4258.62，WebView2 SDK 1.0.4258.31，Windows SDK .NET Ref 10.0.17763.56。全局原有 Desktop Runtime 8.0.17 / 9.0.5 未修改。

最终测试结果和资源样本见下一节的证据记录。编译使用本地缓存离线还原；实际原生运行使用正常用户权限。开发工具沙盒中的合成控件创建出现 `0x8000FFFF`，正常用户运行可初始化；这属于本次工具沙盒条件，不在应用中添加提权或降级。

修复了以下实际发现的问题：合成控件缺失 `Microsoft.Windows.SDK.NET` 运行依赖；打包资产路径丢失 `assets/`；防御快照造成窗口边界未保存；延迟保存覆盖较新的前端设置；初始化重复调用/失败清理；退出后异步回复；WebView2 消息回调直接打开模态文件选择器造成重入。文件选择器改为 Dispatcher 延后显示，符合[微软线程模型要求](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model)。

窗口恢复的测试采用真实 Mini 鼠标点击再注入 Esc 键，验证 Windows 前台行为。纯程序化后台调用无法保证获得 Windows 前台许可，此情况不能当作用户点击失败或用模拟前台替代。

## 6. 证据与未完成验证

| 验收项 | 实际结果与证据 |
| --- | --- |
| Release 编译与发布 | 成功，0 错误、0 警告；全部官方开发包由本地缓存离线还原。已生成 `artifacts/win-x64/DesktopHub.Windows.exe`，PE machine=0x8664（AMD64），依赖运行时发布；正常用户配置下独立 EXE 已成功启动并完成原生握手 |
| 核心测试 | 17 项配置/校验/错误测试通过；另加真实受控程序启动与正常关闭，共 18 项通过 |
| 前端测试 | 桥接与桌面适配 17 项、原型回归 7 项，合计 24 项通过 |
| 原生完整链路 | 最终 20 项通过，含实际文件选择器、真实创建进程、真实缺失文件错误、原生消息安全检查、持久化与窗口行为；[原始结果](m1-evidence/native/results.json) |
| Mini | 真正独立 HWND，默认 62 × 314 DIP、非置顶；实际点击可从受控前台程序唤出 Workspace |
| Dock | 不同 HWND，默认 338 × 591 DIP；实际显示、置顶、收起；300 ms 尚未展开，最终采样约 600 ms 展开（默认 500 ms，加 100 ms 计时采样粒度） |
| Workspace | 第三个 HWND，1536 × 904 DIP，主工作区 1600 × 952 DIP；暗/亮主题真实渲染 |
| 透明效果 | Mini/Dock WPF 透明宿主与 PNG 中大量 0<alpha<255 像素均验证；保持 HTML 半透明表面。未验证桌面 Acrylic 模糊、壁纸嵌入或所有 GPU/远程桌面条件 |
| Esc / 焦点 | 从实际 Mini 点击打开后，真实 Esc 输入隐藏 Workspace，原先受控程序 HWND 恢复为前台，结果 `restored` |
| 重启与配置 | 多次独立进程启动读取上次保存的主题/程序目标；新 Store 实例重读和窗口坐标持久化检查通过。损坏配置安全恢复由核心测试验证 |
| 离线 | 本地文件映射、CSP 与宿主资源拦截；主动导航远程 URL 被拒绝，本地 UI 继续可用。未禁用用户网卡或修改防火墙；不声称测过物理断网矩阵 |

截图已检查： [Mini](m1-evidence/native/mini.png)、[Dock](m1-evidence/native/dock.png)、[暗色场景中心](m1-evidence/native/workspace-dark.png)、[浅色场景中心](m1-evidence/native/workspace-light.png)、[真实启动成功](m1-evidence/native/launch-success.png)、[真实启动失败](m1-evidence/native/launch-failure.png)。历史修复过程的失败证据保留在 `run-*` 目录，顶层文件是最终结果。

资源采样是短时间技术验证，不是性能验收。Mini 初始化后的私有字节样本为宿主约 153.5 MiB、WebView2 进程约 207.7 MiB。初版三个窗口都使用合成控件，在截图后隐藏的样本中宿主私有字节约 402 MiB、WebView2 约 524 MiB；改用标准 Workspace 和隐藏挂起后，对应样本约 297 MiB / 485 MiB。测试包含高 DPI 图形捕获、截图和受控启动，会影响数值，不能作为纯常驻基准。各进程工作集包含共享页面，禁止简单相加解释为实际物理内存总量。最终采样见 [Mini 采样](m1-evidence/native/mini-idle-memory.json) 与 [三窗口采样](m1-evidence/native/three-surfaces-loaded-hidden-memory.json)。独立发布 EXE 在普通配置下启动 4 秒后宿主私有字节约 153 MiB，未执行截图。

**仍待验证或未达成的能力：** Windows 10 实机；其他 DPI 与多显示器；全屏游戏、排除名单和显卡/远程桌面的透明兼容；焦点恢复在前台权限被 Windows 拒绝/原窗口消失时的完整矩阵；人工拖拽手感；长时间 CPU、GPU、内存及冷启动基准。当前资源开销仍偏高，不能宣称“轻量化性能验收通过”。合成控件在特殊环境中初始化失败时，目前给出错误和日志，尚无自动不透明降级。

2026-10-09 追加了可重复运行的 M1 资源基线测试：同一发布 EXE 用独立测试配置切换真实窗口，每态稳定 15 秒、测量 60 秒。首次 Mini、Dock、Workspace 首页、使用后 Mini 的进程组平均私有提交内存分别约 373.8、524.2、687.3、686.0 MiB；整机归一化 CPU 平均约 0.428%、0.357%、0.363%、0.410%。Dock 发生一次 GPU 进程更替，观测内存峰值约 991.8 MiB，并排除 2 个无法完整计算的 CPU 区间。返回 Mini 的资源保留问题已记录；这仍不是长期性能验收。各版本变化记录、配置条件、计算公式与完整 JSON/CSV 证据见 [Resource_Usage_History.md](Resource_Usage_History.md)。

## 7. 启动、调试与依赖

```powershell
# 当前机器所有开发依赖已缓存，可离线执行
.\scripts\build.ps1 -Offline
.\scripts\run.ps1 -Offline

# 新机器首次获取开发依赖；不改系统 PATH、不需管理员权限
.\scripts\bootstrap.ps1

# 发布为依赖运行时的独立目录
.\scripts\publish.ps1 -Offline

# 原生验收会操作本应用窗口并短暂移动鼠标，请在空闲桌面运行
.\scripts\test-native.ps1 -SkipBuild -CoreFixtureIntegration
```

如机器策略禁止脚本，可用 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -Offline`，仅对本次 PowerShell 进程生效。也可直接启动 `artifacts\win-x64\DesktopHub.Windows.exe`，保持 `UI/`、DLL 和 `runtimes/` 相邻，不可只复制 EXE。

开发：项目内 SDK 或兼容 .NET 8 SDK、Node.js（仅前端测试）、官方 NuGet 缓存。可在 Visual Studio 中打开 `src/DesktopHub.Windows/DesktopHub.Windows.csproj` 调试。真实界面进入场景中心选择普通程序；切主题/位置验证本地配置，退出并重启再检查。日志位于 `%LOCALAPPDATA%\DesktopHub\host.log`。

运行：Windows 10 1809+ / Windows 11 x64、.NET Desktop Runtime 8 x64、Microsoft Edge WebView2 Runtime。本机都已具备。发布目录不包含整个 .NET 或 WebView2 Runtime，不是无需依赖的单文件包；尚未制作 MSI/MSIX、签名或自包含安装器。缺失 WebView2 时显示错误，不擅自下载或管理员安装。Node.js、在线服务器及打开 Chrome/Edge 浏览器都不是运行依赖。

## 8. M2 建议（本轮未实施）

1. 先迁移至 .NET 10 LTS，并扩展 Windows 10/11、100/125/150/200% DPI、显示器变化、前台窗口关闭/最小化及远程桌面的回归矩阵。
2. 为透明合成控件建立稳定降级与资源预算；评估隐藏 WebView 的挂起/释放、长时间内存与 GPU 占用，验证 Mini 的低干扰层级和真正桌面效果。
3. 在现有协议上增加明确类型的场景动作/执行实例、依赖与等待；保留原生层的权限和参数边界。脚本与其他动作必须另行按 M2 范围实施。
4. 再独立实现授权文件索引、监测与标签持久化，不以当前原型演示 localStorage 作为正式数据模型。
5. 托盘、全局快捷键冲突、游戏与排除保护及安装/便携分发分别验收；不用未公开壁纸接口替代稳定窗口控制。
