# Desktop Hub 完整体验改进实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** 完成全部体验建议，保留现有 Windows 功能和用户数据，并交付可运行发布版本。

**Architecture:** 复用 C# / WPF / WebView2，Core 提供独立元数据和业务服务，Windows 提供受控文件、窗口和桥接，desktop 提供紧凑交互。分任务串行实现与审查，统一接口由本计划定义。

**Tech Stack:** .NET 8、WPF、WebView2、原生 JavaScript / CSS、现有离线构建与零依赖测试。

**Spec:** `docs/superpowers/specs/2026-10-13-desktop-hub-full-usability-design.md`

## Global Constraints

- 保留 18 DIP 圆角、四角透明、深浅与系统主题、不透明度、原生缩放、托盘、独立全局快捷键、全屏保护、自启及桌面图标恢复策略。
- 文件留在原处；整理只写本应用元数据。正常用户配置不作为测试夹具。
- 设置 schema 5、资源 schema 2、最近使用和项目 schema 1；保留所有旧字段与 ID。
- 搜索 150ms 防抖、最多 50 项；批量应用最多 100 项，批量资源最多 500 项；Mini 最多 4 个入口。
- 紧凑正文不小于 13px；验证 640×480 / 960×640 DIP、100% / 200% DPI、深浅主题。
- 预览并发最多 2、缩略缓存 32 MiB、单项解码 16 MiB、图片最长边 1600、PDF 首页 1200 像素。
- 轻量模式隐藏 60 秒释放，默认快速唤出；布局默认等待 15 秒，不自动提权。
- 桥接仅稳定实体 ID 或宿主授权令牌；严格字段校验，不开放任意命令或文件读写。
- 用户已明确“开始实现”，本次直接执行，不重复请求实施授权；在本地 `codex/full-usability` 分支保留并记录现有改动，不推送。

## Review Focus

- 用户输入法组合期间回车不得启动搜索结果；归属 Task 2。
- 后台刷新时已选择实体消失，批量提交不能作用于另一个实体；归属 Task 3。
- 收藏与 Dock 固定四种组合，以及旧固定关系迁移后保持原顺序；归属 Task 1 / 4。
- 文件选择器、草稿、导入确认与请求返回竞争时不能销毁窗口或接收旧结果；归属 Task 7 / 8。
- 同名多窗口、显示器移除及高权限窗口不能被错误移动；归属 Task 8。

## Working rules

每个任务先写有意义的失败测试并运行，再实现、运行对应测试、更新记录。任务实现者不再派生代理；控制者分发独立审查。按互不重叠的文件范围并行实施，同一文件只由一个实现者写入，提交串行进行。Windows 缺少 bash，以 PowerShell / Node 建立等价的任务 brief、报告和审查包，存于已忽略的 `artifacts/usability-development/`。

### Task 1: 数据迁移与业务基础

**Files:** 修改 `src/DesktopHub.Core/AppSettings.cs`、`SettingsValidator.cs`、`SettingsStore.cs`、`AppLibraryService.cs`、`Resources.cs`、`ResourceLibraryService.cs`；创建 `UsabilityModels.cs`；测试 `tests/DesktopHub.Core.Tests/Program.cs`、`tests/DesktopHub.Resource.Tests/Program.cs`。

**Interfaces:** `AppSettings.UiDensity`、`PerformanceMode`、`AutoCloseDock`、`AppGroups`；`AppEntry.Favorite`、`GroupId`；`AppGroup(Id,Name,DisplayOrder)`。场景新增 `EditorMode`、`ExecutionMode` 和可选 `WindowLayout`。资源新增 `SavedResourceFilter`，`ResourceLibrary.SavedFilters`；`ResourceLibraryService.BatchAsync(IReadOnlyList<string>,string,IReadOnlyList<string>)`、`SaveFilterAsync(SavedResourceFilter)`、`DeleteFilterAsync(string)`。`AppLibraryService.SetFavorite(string,bool)`、`SaveGroup(string?,string)`、`RemoveGroup(string)`、`MoveToGroup(string,string?)`。

- [x] 迁移测试：schema 4 收藏从旧 Dock 状态迁移，schema 5 的独立收藏不被固定覆盖，旧依赖和提示偏好保留；未知 schema 拒绝写入且原文件保留。
- [x] 资源测试：500 上限、无效 ID 原子失败、增量标签不改变其他标签、保存筛选重启与删除标签失效。
- [x] 运行上述测试观察新行为缺失，然后实现规范化、深克隆、迁移与业务方法。
- [x] 运行 `.tools/dotnet/dotnet.exe run --project tests/DesktopHub.Core.Tests -c Release` 和资源测试（使用项目离线源）；预期全通过。
- [x] 记录接口与验证，提交该任务文件。

### Task 2: 统一搜索与紧凑界面

**Files:** 创建 `src/DesktopHub.Core/UnifiedSearchService.cs`、`desktop/search.js`、`desktop/usability.js`、`desktop/usability-v2.css`；修改 `desktop/index.html`、`native.js`、Windows `NativeBridge.cs`；新增搜索 / 界面测试。

**Interfaces:** `SearchEntity(Type,Id,Name,Detail,Status,Favorite)`；`UnifiedSearchService.Search(IEnumerable<SearchEntity>,string,string,int=50)` 返回稳定排序实体。桥接 `search.query {query,type}`；前端 `DesktopHubSearch.open()`、`refresh()`，`DesktopHubUsability.apply()`；从当前 settings / resource snapshot 汇集，不额外扫描文件。项目汇集由 Task 6 扩充。

- [x] 测试名称 / 首字母 / 同名异类排序、50 上限、旧结果和 IME；先失败后实现。
- [x] 实现本地汉字首字母匹配，许可证随分发；不限制成示例字表。
- [x] 接入顶部与 Ctrl+K、键盘 / 焦点恢复、失效操作、类型筛选。
- [x] 紧凑首页和文件库，提供持久密度设置；保留圆角及主题。
- [x] 运行新测试与 `node --test desktop/tests/*.test.mjs prototype/tests/state.test.mjs`，记录截图验收项后提交。

### Task 3: 批量资源交互与保存筛选

**Files:** 修改 `desktop/resources.js`、`resources.css`、`src/DesktopHub.Windows/ResourceBridge.cs`；测试 `desktop/tests/native-resources.test.mjs`。

**Interfaces:** 消费 Task 1 的 BatchAsync / SavedFilters；桥接 `resources.batch {resourceIds,operation,tagIds}`、`resources.filterSave {filter}`、`resources.filterDelete {id}`；新增查询排序字段与筛选失效状态。

- [x] 添加跨页选择、条件切换清空、后台实体消失、增量批量操作测试，先运行失败。
- [x] 实现选择工具条、标签选择确认、收藏及同类建议处理、保存 / 编辑 / 删除筛选。
- [x] 桥接验证 500 项及允许操作，后台刷新保留滚动；失效清理仍使用独立操作。
- [x] 运行资源 Core / 前端测试，提交并记录结果。

### Task 4: 应用、入口与引导

**Files:** 修改 `desktop/desktop.js`、`onboarding.js`、样式、`src/DesktopHub.Core/ApplicationImportService.cs`、Windows `NativeBridge.cs`、`HubWindow.cs`；测试应用 / 引导与 Core。

**Interfaces:** 消费 Task 1 分组 / Favorite；桥接 `apps.favorite`、`apps.groupSave`、`apps.groupRemove`、`apps.moveGroup`；`apps.scan` 增加 `startMenu` 和宿主拖放令牌模式。原生拖放事件 `apps.dropPreview` 仅含扫描对象。

- [x] 测试桌面 / 开始菜单统一去重、原生多选 / 令牌失效、组排序、收藏与固定四种组合，先失败。
- [x] 实现拖入、分组和拖动 / 键盘排序，保留现有桌面提示偏好与批量导入。
- [x] 简化 Dock 空分区并移出低频设置，按独立应用请求成功执行可选自动收起。
- [x] 引导调整为欢迎 → 添加启动应用 → 固定 → 可选文件 → 常驻设置 → 完成；旧完成记录不重播。
- [x] 运行 Core、desktop-adapter、onboarding 测试，提交与记录。

### Task 5: 简单场景、结果与失败重试

**Files:** 修改 `src/DesktopHub.Core/Scenes.cs`、`SceneExecutionService.cs`、`desktop/scenes.js`、样式、Windows `NativeBridge.cs`；测试场景 Core / 前端。

**Interfaces:** 消费 Task 1 模式字段；`SceneExecutionService.Retry(string executionId)` 使用原快照与前次完成状态；桥接 `scenes.retry {executionId}`；简单顺序模式排序重建 completed 依赖。

- [x] 测试简单排序影响真实顺序、旧高级依赖保留、转换确认与快照失败重试不再启动成功项，先失败。
- [x] 实现批量选应用、简单 / 高级模式和执行方式；循环依赖继续校验。
- [x] 当前视口内结果面板、分层文案、修复入口、保留 Mini / Dock 摘要。
- [x] 运行场景测试和前端全部测试，记录后提交。

### Task 6: 最近使用与项目集合

**Files:** 创建 Core `ActivityService.cs`、`ProjectService.cs`、Windows `ActivityProjectBridge.cs`、`desktop/activity-projects.js` 及测试；修改 host 初始化、动作成功记录与搜索汇集。

**Interfaces:** `ActivityService.Record(string type,string id,string result)`、`Snapshot()`、`Clear()`；`ProjectService.Save(Project)`、`Remove(string)`、`Snapshot()`；文件 schema 1，原子存储。桥接 `activity.query/clear`、`projects.query/save/remove`；项目项只实体 ID 或验证 HTTP(S) URL。

- [x] 测试50项去重、失败不记录、删除实体不可执行、共享项目引用、项目删除只解除关联，先失败。
- [x] 实现首页6项与完整最近页、项目列表 / 详情 / 固定首页、按类型添加引用与排序。
- [x] 项目纳入统一搜索；浏览项目不批量打开任何关联内容。
- [x] 运行新增 Core 与前端测试，提交并记录。

### Task 7: 受限图片与 PDF 预览

**Files:** 创建 Windows `ResourcePreviewService.cs`、`desktop/preview.js`、测试；修改 ResourceBridge、宿主与发布内容。

**Interfaces:** `ResourcePreviewService.RenderAsync(string resourceId,bool thumbnail,CancellationToken)` 返回受控 PNG 与元信息；`Cancel(string requestId)`；桥接 `resources.previewContent {resourceId,thumbnail,requestId}`、`resources.previewCancel {requestId}`。

- [ ] 测试资源授权、损坏 / 移走 / 云占位、尺寸预算、取消及缓存键，先失败。
- [ ] 原生 WPF 图像解码与 Windows PDF 首页渲染，遵守全部预算；只可见图标加载缩略。
- [ ] 单项选择与空格 / Esc、输入框避让、不可预览提示及外部打开。
- [ ] 真实图片与 PDF 原生验收、前端测试、缓存测量，提交与记录。

### Task 8: 窗口布局与轻量生命周期

**Files:** 创建 Windows `SceneWindowLayoutService.cs`；修改 `HubApplication.cs`、`HubWindow.cs`、`NativeBridge.cs`、`ResourceUsageProbe.cs`、设置和场景界面；新增生命周期 / 窗口布局测试。

**Interfaces:** `Capture(string sceneId)` 枚举可选窗口；`Save(string sceneId,IReadOnlyList<WindowLayoutEntry>)`；`RestoreAsync(string sceneId,CancellationToken)` 返回每项结果；桥接 `scenes.layoutCapture/layoutSave/layoutRestore`。仅会话识别窗口句柄，持久记录 exe / 标题 / 应用 ID 与相对工作区。

- [ ] 测试多候选不自动移动、DPI / 显示器回退、最大化、高权限 / 全屏跳过，先失败。
- [ ] 实现保存与显式 / 场景执行后恢复，独立反馈布局结果。
- [ ] 测试隐藏60秒释放、草稿 / 文件选择器 / 导入 / 写入忙状态保护、重建导航和后台执行，先失败后实现。
- [ ] 测量与修改前构建同条件的内存 / CPU / 进程数、冷热唤出，目标与实测分开；提交并记录。

### Task 9: 全量验收、审查与发布

**Files:** 更新 README、实施报告、验收脚本与截图证据，按核对结果更新根目录启动快捷方式。

- [ ] `.\scripts\publish.ps1 -Offline -OutputDirectory artifacts/win-x64-full-usability` 全量测试通过。
- [ ] 原生实际 WebView2 验收全部功能、迁移和重启，在隔离配置中截图深浅、大小与 DPI。
- [ ] 独立全范围审查规格 / 代码质量并处理重要问题；修复采用失败复现测试。
- [ ] 发布文件与验收 DLL / UI 哈希一致后更新启动入口，保留真实环境限制和性能目标未达项。
- [ ] 提交并提供可运行版本及简洁交付说明，不将任何未验收项宣称完成。
