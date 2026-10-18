# 发布 Desktop Hub

## 用户安装与更新

从本仓库 Releases 下载 DesktopHub-win-Setup.exe（以实际生成文件名为准）。安装版包含 .NET 8 x64，无需另装 .NET。缺少 WebView2 时安装程序会检测并下载微软运行环境，需要联网，可能出现系统授权提示。取消或失败时重新运行 Setup，或从微软官网安装 WebView2 Evergreen Runtime 后重试。

普通启动打开 Workspace，开机自启使用 --startup 后台常驻。设置 → 系统与恢复 → 软件更新可检查、下载和确认重启安装。检查失败不阻止使用；检查至少间隔一分钟。更新保留 %LOCALAPPDATA%/DesktopHub/。旧便携版用户需手动安装首个支持更新的安装版，使用相同 Windows 账户继续读取已有数据。

安装包暂未配置代码签名。MIT 开源许可免费；代码签名与项目许可是两件事。

## 本机打包（PowerShell 7）

```powershell
pwsh -File ./scripts/bootstrap-release.ps1
pwsh -File ./scripts/package-release.ps1 -Version 0.3.1 -Offline
```

输出在 artifacts/releases/0.3.1/，包括 Setup、便携包、完整更新包、更新索引和 SHA256SUMS.txt。更新需要配套更新包与 releases.win.json，不能只上传 Setup。输出目录必须为空或不存在。-SkipTests 仅用于测试已通过后的重复打包。

可用 -BaselineDirectory artifacts/update-baseline 提供上一版本更新产物以生成增量。无基线仍能生成完整更新包。开发用 scripts/publish.ps1 继续提供依赖外部 .NET 的便携产物；正式面向普通用户使用本安装版。

## GitHub 发布

1. 审核并合并代码到主分支，确认项目仓库对用户公开。
2. 更新版本并推送与版本一致的三段稳定版本标签，例如 v0.3.1。
3. Windows Actions 执行测试、自包含构建、打包并创建草稿 Release。读取已有公开稳定更新基线，接口或下载失败会停止，不会当成首次发布。
4. 检查附件及升级验收，填写更新说明，手动发布 Release。草稿和预发布版本不会提供给稳定客户端。

客户端仅访问固定公开 GitHub Releases，不内置个人令牌。GitHub 无法访问或限流时稍后重试。

## 验证要求

- 自动化：pwsh -File scripts/build.ps1 -Offline，包含更新状态机与前端交互测试。
- 隔离环境：首次安装、重复启动唤出 Workspace、开机自启保持后台、两个递增版本升级、设置及资源索引保留。
- 缺少 WebView2 的机器：安装、取消和重试。不要为测试卸载日常电脑依赖。
- GitHub Actions、真实 GitHub 更新源、缺 WebView2 环境和完整两版本升级只有实际执行后才算通过；本机打包成功不代表这些项目已通过。

## 许可

根目录 LICENSE 为 MIT，Copyright (c) 2026 Thedust-YCL。分发保留许可及第三方声明；安装包一并携带。第三方组件见 THIRD_PARTY_NOTICES.md 与安装目录 licenses/。

## 本次本机验收记录（2026-10-18）

- 完整自动化构建通过：前端 266 项，更新状态机 7 项，后端与生命周期等现有测试通过。
- WebView2 真实界面：5 项通过，包含中文状态、重启确认、英文进度及最小窗口宽度；截图位于 artifacts/update-ui/。
- 隔离便携更新引擎：0.3.1 → 0.3.2 测试包升级通过，清单和二进制版本一致，测试设置与资源文件 SHA256 保持不变；未自动重启或访问 GitHub。
- 0.3.2-upgrade-test 仅是本机验收产物，正式交付仍为 0.3.1-delivery。
- 尚未执行真实 GitHub Actions、公开源更新、全新系统 Setup 安装、缺失 WebView2 安装/取消及更新后重启的完整链路；正式发布前仍需验收。
- 原有隔离原生发布验收最终通过：Task4–6 56+5 项、Task7–8 38 项，共 99 项；56 个发布负载文件核对通过。证据：artifacts/usability-release/20261018T075523718Z/。
- 根目录“打开 Desktop Hub.lnk”已指向 artifacts/desktop-hub-0.3.1/Desktop Hub.exe；旧快捷方式备份保存在 artifacts/。
