# Workspace 圆角与不透明度 · 2026-10-10

Workspace 外框改为 18 DIP 圆角，保留细边框、原生拖动和八个边缘/角落的缩放入口。四角外侧透明，深浅主题切换不再填充矩形背景。

“设置 → 外观与主题”在主题选项下提供独立的 Workspace 不透明度滑块：0–100%，默认 100%，拖动显示百分比，释放后保存。整个窗口随之改变透明程度，Mini 偏好不受影响；原有“透明效果”开关继续用于 Mini / Dock。新增 `workspaceOpacity` 是 schema 4 的可选字段，旧配置缺省为 1。

选择 0% 后可从托盘“打开设置”恢复：保存值低于 10% 时，此入口暂时以 100% 显示工作空间，不改写保存值，调整滑块后按新值显示。普通唤出与重启按保存的不透明度显示。

Workspace 改为 WebView2CompositionControl，并使用透明原生窗口、透明网页背景和随尺寸变化的 WPF 圆角裁剪，使 WebView 内容参与整个窗口的透明混合。该控件的设计依据见 [Microsoft WebView2 Composition 规范](https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/WPF_WebView2CompositionControl.md)。

## 验证

- 完整离线构建成功，0 警告、0 错误；核心 29、场景 17、资源 28、系统 23、生命周期 8、前端 69，共 174 项基础测试通过。
- 最终原生验收为首个宿主 68 项、独立重启后累计 72 项通过。使用隔离配置、测试文件和纯色测试背景窗口，未修改个人配置或资源库。
- 屏幕像素检查验证四角露出测试背景，并验证 65% 的真实窗口颜色混合；同时检查独立 Mini 偏好、0% 恢复路径、深浅主题和缩放后裁剪尺寸。截图人工检查通过，滑块在默认设置视口可见。
- 修正隔离测试准备逻辑，重复执行时重置分类，避免上次创建的测试分类干扰结果。

最终证据：[原生验收结果](usability-evidence/rounded-workspace/run-20261010T140307303Z/run.json)、[浅色圆角框架](usability-evidence/rounded-workspace/run-20261010T140307303Z/workspace-native-rounded-light.png)、[65% 不透明度](usability-evidence/rounded-workspace/run-20261010T140307303Z/workspace-native-opacity-65.png)。截图使用本机 200% DPI；不同显示器 DPI 与物理边缘拖动仍可补充人工验收。

## 运行

新版发布到 `artifacts/win-x64-rounded/`，根目录“打开 Desktop Hub.lnk”指向此版本。正在运行的旧实例需从托盘“完整退出”后重新打开快捷方式。

发布命令：`scripts/publish.ps1 -Offline -SkipTests -OutputDirectory artifacts/win-x64-rounded`。
