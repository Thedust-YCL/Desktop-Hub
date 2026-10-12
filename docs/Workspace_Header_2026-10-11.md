# Workspace 顶部操作 · 2026-10-11

移除顶部完全退出按钮，只保留关闭按钮。关闭 Workspace 后 Mini / 托盘继续常驻，完整退出仍可通过托盘或系统设置执行。

顶部主题按钮按当前显示颜色切换到另一种颜色，仅保存深色或浅色。即使先前设置为跟随系统，也从当前显示颜色切到相反颜色；按钮提示直接显示“切换为浅色”或“切换为深色”。设置中的跟随系统 / 深色 / 浅色三项继续保留。

沿用现有顶部图标、Quiet Desktop 间距、圆角、透明度和缩放行为，只在正式 `desktop/` 适配层修改。

构建通过，0 警告、0 错误；69 项前端检查通过。隔离 Windows 验收为首次宿主 74 项、独立重启后累计 78 项通过，包括设置选择跟随系统、快捷切换按实际系统颜色取反、反复深浅切换、关闭后仍常驻、透明圆角及原有资源功能。截图人工检查通过。

证据：[验收结果](usability-evidence/header-controls/run-20261010T184147036Z/run.json)、[设置保留跟随系统](usability-evidence/header-controls/run-20261010T184147036Z/header-settings-system.png)、[浅色顶部与关闭按钮](usability-evidence/header-controls/run-20261010T184147036Z/workspace-native-rounded-light.png)。首次验收的选择器误将分段控件容器计入按钮数，修正为仅统计按钮后通过。

新版发布到 `artifacts/win-x64-header/`，根目录“打开 Desktop Hub.lnk”指向此版本。若旧实例正在运行，从托盘完整退出后再打开快捷方式。
