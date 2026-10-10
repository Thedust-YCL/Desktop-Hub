# Desktop Hub · Windows M2.1

复用 M1 / M1.5 的 C#、WPF、WebView2 三窗口、应用库与原生启动服务，新增场景组合启动。应用仍可独立启动；场景可组合应用、网页、文件、文件夹和已有 BAT/CMD/PowerShell 脚本，设置依赖、延迟、进程与窗口等待，并在三个界面查看真实结果。移除只删除 Desktop Hub 记录，不卸载或删除软件。

在本目录运行：

```powershell
.\scripts\build.ps1 -Offline
.\scripts\run.ps1 -Offline
```

当前机器的 SDK 和开发依赖已准备在 `.tools/`。新机器先运行 `scripts/bootstrap.ps1` 下载并校验开发依赖。普通运行需要 .NET Desktop Runtime 8 x64 和 WebView2 Runtime；无需 Node.js、浏览器页面或本地服务器。详情见 [M2.1 实施报告](docs/M2.1_Implementation_Report.md)、[场景通信扩展](docs/M2.1_Bridge_Protocol.md)、[M1.5 历史报告](docs/M1.5_Implementation_Report.md) 和 [M1 历史报告](docs/M1_Implementation_Report.md)。

打开 Mini 顶部图标进入 Workspace，在“我的应用”选择“添加应用”，之后直接点击应用图标启动。“管理应用”可编辑、排序和移除；在“场景中心”新建场景，从应用库选择动作，保存后执行。固定入口管理可分别排列 Mini 与 Dock，Dock 图标支持紧凑/标准/大尺寸。Mini 最多显示四个混合入口，应用库与场景库容量不受此限制。按 Esc 关闭 Workspace；右上角退出图标完整退出。资源页继续保留原型内容。

配置保存在 `%LOCALAPPDATA%\DesktopHub\settings.json`，schemaVersion=3 保留既有应用库、固定入口和可识别的 M1 单目标迁移。场景动作通过稳定应用 ID 读取最新配置。Store/MSIX 及特殊 Shell 快捷方式继续保留原兼容边界；脚本按系统策略执行，提权由 Windows UAC 处理。

M2.1 原生验收：`scripts/test-m21-native.ps1 -SkipBuild`，使用隔离测试配置和受控目标。构建命令会运行原有核心、场景调度/存储和前端自动化测试。验收结果与人工待验证项见 M2.1 报告。

可重复运行的 M1.5 原生验收：`scripts/test-m15-native.ps1 -CoreFixtureIntegration`，使用隔离测试配置、真实文件选择器和两次独立进程启动，短暂打开五个系统 GUI 程序后关闭测试创建的窗口。证据存于 `docs/m15-evidence/native/`，不覆盖 M1 记录。

各版本内存/CPU 基线见 [资源占用变化记录](docs/Resource_Usage_History.md)。使用 `scripts/measure-resources.ps1 -SkipBuild` 可重复测量已发布程序的 Mini、Dock、Workspace 和返回 Mini 情境，证据按运行时间归档。

以下保留 M0 原型的独立使用说明。

# M0 可交互 UI 原型

这是 Desktop Hub 第一阶段的本地高保真交互原型，用于评审 Mini、Dock、Workspace 及二级业务页面的视觉与交互。实现采用 HTML、CSS、原生 JavaScript，不依赖前端框架、构建工具、CDN、账户或后端。

**本轮唯一正式 UI 交接规格是 [Desktop_Hub_UI_Spec_v0.7.md](docs/Desktop_Hub_UI_Spec_v0.7.md)。** `docs/references/` 的三张图片仅作视觉参考，`PRD.md` 仅作背景资料。

## 如何启动与查看

### 直接打开

双击 `prototype/index.html`，使用 Edge、Chrome 等现代桌面浏览器打开即可。首次会进入欢迎与模拟扫描确认流程。

### 本地静态服务器

如果已安装 Node.js，在仓库根目录执行：

```powershell
node prototype/serve.mjs 4173
```

然后打开 [http://127.0.0.1:4173](http://127.0.0.1:4173)。停止服务器可在终端按 `Ctrl+C`。服务器只绑定本机 `127.0.0.1`，只提供 `prototype/` 内的静态文件，不访问外部服务；无需 `npm install`。

建议以常见桌面窗口尺寸查看。顶部“原型演示”控制条用于切换 Mini / Dock / Workspace、模拟前台应用、主题及首次引导，不代表正式产品中存在该控制条。

## 页面与交互

| 页面 / 状态 | 可评审内容 |
| --- | --- |
| Mini | 2–4 个固定场景、名称提示、模拟启动、进入 Dock / Workspace；左 / 右 / 顶部、尺寸与显示模式设置 |
| Dock | 屏幕边缘默认约 500 ms 悬停展开；固定场景、模拟进度与状态、全部场景及 Workspace 入口；运行结束保持展开 |
| Workspace 首页 | 近全屏 Launchpad，六个常用功能入口、搜索、设置、关闭与固定场景 |
| 场景中心 | 新建、编辑、重命名、排序、删除、固定到 Mini 与模拟启动 |
| 场景编辑器 | 左侧动作列表、右侧动作参数；程序 / 网页 / 文件 / 文件夹 / 脚本；执行方式、等待、依赖、超时、重复策略与循环依赖提示 |
| 文件资源库 | 列表 / 文件类型图标网格 / 标签分组三视图；模拟新文件、建议标签确认、详情、收藏、临时文件夹浏览 |
| 标签中心 | 主题 / 时期 / 自定义标签；创建、修改、删除与按标签浏览资源 |
| 收藏与快速搜索 | 收藏管理；按文件名、正式标签及标签筛选查找模拟索引 |
| 失效资源中心 | Missing / PendingRepair / SourceUnavailable 筛选，单项模拟修复，确认后批量清理仅索引 |
| 设置 | 跟随系统 / 深 / 浅主题、减少动画、透明度；Mini / Dock、独立快捷键、全屏保护与排除程序、来源与递归、模拟自启 / 托盘、桌面图标恢复策略 |
| 首次运行引导 | 欢迎 → 可勾选扫描预览 → 确认模拟索引 → Mini 位置 → Launchpad → 创建第一个场景 |

默认跟随系统明暗主题。可用顶部主题按钮或“设置 → 外观与主题”手动切换深色、浅色，评审两套界面。所有页面使用同一套本地 SVG 图标；文件网格仅显示文件类型图标，没有图片或 PDF 内容缩略图。

## 建议评审顺序

1. 完成首次引导，观察确认扫描前索引不入库；选择 Mini 位置并进入 Launchpad。欢迎页可直接创建第一个自定义场景。
2. 用顶部控制条进入 Mini，点击“创作”，观察 Dock 中明确标为“模拟”的待执行、执行中、成功、跳过与失败反馈。创作示例为“加速器 → Clash → ChatGPT → Edge → DSH”；游戏与休闲提供另外两组示例。
3. 在场景中心新建或编辑场景，添加动作、配置等待和依赖、保存后模拟运行。尝试循环依赖，确认保存被阻止。
4. 在资源库切换三个视图，确认“设计参考图.png”的建议标签，再去标签视图检查正式关联。也可使用“模拟新文件”入口重现该流程。
5. 在失效资源中心修复一项，确认标签与收藏仍保留；批量清理需先查看确认对话框，清理只作用于模拟索引。
6. 选择模拟 Edge 前台，打开 Workspace 后按 `Esc`，查看返回模拟前台窗口的反馈；切换全屏游戏验证悬停保护。
7. 在设置中切换主题、位置、透明度和动效，尝试快捷键冲突；首次启用“隐藏桌面图标”时先选择恢复策略。

完整点击验收清单与当前验证记录见 [prototype/ACCEPTANCE.md](prototype/ACCEPTANCE.md)。

## 快捷键

| 组合 | 原型内行为 |
| --- | --- |
| `Alt+D` | 独立展开 / 收起 Dock |
| `Alt+W` | 独立唤出 / 关闭 Workspace |
| `Ctrl+K` | 进入快速搜索 |
| `Esc` | 关闭可关闭的对话框，或关闭 Workspace / Dock，回到模拟桌面 |

快捷键只在浏览器页面获得焦点时参与演示，不注册 Windows 全局快捷键。Dock 与 Workspace 组合可在设置中修改并进行冲突校验；若浏览器或系统占用组合，可使用顶部同名控制按钮。首次引导的必要确认步骤不能被 `Esc` 或点击遮罩跳过。

## 模拟边界与本地状态

- 场景启动、进程等待、窗口等待、脚本执行、文件打开、文件定位、索引扫描和修复均为本地模拟，并在界面中注明。
- 示例程序路径与 URL 是可编辑的演示文本，不会调用软件、运行脚本或自动打开外部网站。
- 不读取、遍历、监控、移动、删除或修改真实桌面文件。不隐藏 Windows 桌面图标，不修改系统启动项或托盘，不切换真实前台进程。
- 场景、标签、收藏、索引、来源和设置使用浏览器 `localStorage` 保存。直接文件打开与本地服务器的存储相互独立；更换浏览器也会产生独立状态。若浏览器禁用存储，本次更改仍可用于当前页面演示。
- “设置 → 系统与恢复 → 重置演示数据”可恢复示例并重新进入首次引导。只清除本原型的本地状态，不影响磁盘文件。重播引导保留已有模拟索引及其标签 / 收藏，再次确认只合并缺少的模拟记录。

## 文件树

```text
Desktop Hub/
├─ README.md
├─ PRD.md                         # 背景资料
├─ docs/
│  ├─ Desktop_Hub_UI_Spec_v0.7.md # 唯一正式 UI 规格
│  └─ references/
│     ├─ Mini.png
│     ├─ Dock.png
│     └─ Workspace.png
└─ prototype/
   ├─ index.html                # 离线入口
   ├─ styles.css                # 深浅主题、共享 token 与布局
   ├─ app.js                    # 模拟数据、三状态、导航、Dock 悬停与公共组件
   ├─ scenes.js                 # 场景中心、编辑器与模拟执行反馈
   ├─ resources.js              # 文件库、标签、收藏、搜索与失效索引
   ├─ settings.js               # 设置与首次引导
   ├─ serve.mjs                 # 可选的 Node.js 本机静态服务器
   ├─ ACCEPTANCE.md             # 交互验收清单与验证记录
   ├─ tests/
   │  └─ state.test.mjs         # 零依赖逻辑集成自测
   ├─ screenshots/             # 六份浏览器效果截图
   │  ├─ workspace-dark.jpg
   │  ├─ workspace-light.jpg
   │  ├─ mini-dark.jpg
   │  ├─ dock-dark.jpg
   │  ├─ library-dark.jpg
   │  └─ editor-light.jpg
   └─ assets/
      └─ hub.svg                # 本地品牌图标
```

## 已完成验证与截图

运行可复现的零依赖逻辑集成测试：

```powershell
node --test prototype/tests/state.test.mjs
```

最终结果：7 组测试全部通过，0 失败；四个业务脚本与静态服务语法检查通过。测试覆盖七条主要流程、500 ms 悬停计时竞争、Space 快捷键、循环依赖、确认前不写标签关系与修复保留关系。

已在 Codex 内置 Chromium 浏览器实际点击验收：三种界面状态、六个模块、首次引导、三类场景启动、场景新增保存/删除与循环依赖、文件三视图、标签确认与标签 CRUD、临时浏览、失效修复及清理确认、搜索、设置、恢复策略、快捷键、重置。1440×900、1024×768、640×780 下检查布局，六模块未出现页面横向溢出；浏览器控制台没有运行错误。完整记录见验收文档。

截图：[深色 Workspace](prototype/screenshots/workspace-dark.jpg)、[浅色 Workspace](prototype/screenshots/workspace-light.jpg)、[Mini](prototype/screenshots/mini-dark.jpg)、[Dock](prototype/screenshots/dock-dark.jpg)、[文件库](prototype/screenshots/library-dark.jpg)、[场景编辑器](prototype/screenshots/editor-light.jpg)。

## 后续需独立技术验证

M0 展示的是 UI 决策，以下真实 Windows 能力尚未实现，也不由本原型证明可用：

- Mini / Dock 的窗口层级、边缘触发、普通前台软件覆盖与多显示器 / 不同 DPI 行为。
- 全局快捷键注册、系统冲突处理、全屏游戏识别及排除程序规则。
- Workspace 唤出后的焦点恢复、退出及异常恢复时的桌面图标策略。
- 真正的程序 / 窗口等待、依赖调度、超时与脚本执行实例去重。
- 文件来源授权、可靠重命名识别、目录监测、移动后重新关联及大规模索引性能。
- 原生托盘、开机自启、无障碍与键盘导航在最终宿主框架中的落地。
- WPF / WinUI 3 / Tauri 等最终原生框架选择与资源成本对比。

后续应在独立技术验证阶段确认这些能力，再决定原生实现；本轮维持完整可评审的离线 UI 原型。
