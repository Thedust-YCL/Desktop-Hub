# Desktop Hub M1 — WebView2 通信协议 v1

本文依据 `src/DesktopHub.Windows/NativeBridge.cs`、`HubWindow.cs`、`HubApplication.cs`、`src/DesktopHub.Core` 和 `desktop/native.js` 的实现编写。它说明现有接口及边界；实际编译、窗口交互和程序启动验收结果另见 `M1_Implementation_Report.md`，本文不代替运行验证。

## 1. 传输与信任边界

前端通过 `chrome.webview.postMessage()` 发送 JSON 对象，原生宿主通过 `PostWebMessageAsJson()` 返回结果和事件。前端包装入口是 `DesktopHubNative.bridge.request(type, payload, options)`，返回 Promise；事件订阅入口是 `bridge.on(type, callback)`。

UI 使用 WebView2 虚拟主机映射：`https://desktop-hub.local/desktop/index.html?surface=mini|dock|workspace` 对应应用输出目录内的 `UI/desktop/index.html`。该地址由本地文件映射处理，不是在线网站，也没有需要持续运行的本地 Web 服务器。原型资源位于映射目录下的 `UI/prototype/`。

宿主只处理来源 URI 满足 `https`、主机名 `desktop-hub.local`、路径 `/desktop/index.html` 的页面消息；其他来源被忽略。可操作的窗口上下文由 C# 创建窗口时指定，不能由请求 payload 声明。远程导航、弹出窗口、下载和网页权限请求受到限制；未匹配本地映射主机的页面资源请求返回 403。宿主未向 JavaScript 暴露可任意调用的 C# HostObject。

每条请求 JSON 文本最多 16,384 个 .NET 字符，解析深度最多 12。请求根对象及各操作的 payload 禁止未知字段和重复字段。每个窗口最多同时处理 16 个请求，同一窗口不能重复使用尚未完成的请求 id。前端默认超时 12 秒；文件选择操作设置为 120 秒。前端超时不会取消已进入原生层的操作，因此不能把超时理解为“原生操作确定失败”。

## 2. 请求、响应与事件

请求必须是如下对象，字段名区分大小写：

```json
{
  "version": 1,
  "id": "ui-unique-request-id",
  "type": "app.getState",
  "payload": {}
}
```

`version` 必须为整数 1。`id` 必须是 1–96 个字符的字符串。`type` 必须是允许的方法名。`payload` 必须是对象，即使操作没有参数也应传 `{}`。

成功响应：

```json
{
  "version": 1,
  "id": "ui-unique-request-id",
  "ok": true,
  "result": { "opened": true }
}
```

通信或参数失败响应：

```json
{
  "version": 1,
  "id": "ui-unique-request-id",
  "ok": false,
  "error": { "code": "INVALID_PARAMS", "message": "参数无效。" }
}
```

若请求 id 尚未读取或本身无效，错误响应的 `id` 可能为 `null`，无法关联到前端 Promise。包装接口在 `ok: true` 时直接解析 `result`，在 `ok: false` 时抛出带 `code` 的 `NativeBridgeError`。

程序启动是否成功由 `LaunchResult.success` 表示，与通信层 `ok` 分开。例如，启动请求得到真实的“文件不存在”结果时，通信正常，响应为 `ok: true`、`result.success: false`：

```json
{
  "version": 1,
  "id": "ui-launch-request",
  "ok": true,
  "result": {
    "success": false,
    "processId": null,
    "code": "target_missing",
    "message": "所选程序已经不存在，请重新选择。"
  }
}
```

宿主事件没有 `id` 和 `ok`：

```json
{
  "version": 1,
  "type": "state.changed",
  "payload": { "settings": {}, "effectiveTheme": "dark" }
}
```

| 事件 | payload | 用途 |
| --- | --- | --- |
| `state.changed` | `{settings: AppSettings, effectiveTheme: "dark"\|"light"}` | 设置保存、选择程序或系统明暗主题变化后通知已创建的窗口。 |
| `window.navigate` | `{page: string}` | Workspace 打开后进入指定页面。 |

## 3. 原生方法白名单

下表的“全部”表示 Mini、Dock、Workspace 三种原生窗口都可以请求。`{}` 表示不允许任何参数字段。

| 方法 | payload | 允许窗口 | result |
| --- | --- | --- | --- |
| `app.getState` | `{}` | 全部 | `{version, surface, settings, effectiveTheme, settingsWarning, runtimeVersion}` |
| `settings.get` | `{}` | 全部 | `{settings, effectiveTheme}` |
| `settings.save` | `{settings: patch}` | Workspace | `{settings, effectiveTheme}`，返回验证和归一化后的完整配置。 |
| `window.openWorkspace` | `{}` 或 `{page: string}` | 全部 | `{opened: true}` |
| `window.closeWorkspace` | `{}` | Workspace | `{closed: true, focusRestore: string}` |
| `window.openDock` | `{}` | 全部 | `{opened: true}` |
| `window.closeDock` | `{}` | 全部 | `{closed: true}` |
| `window.resizeSurface` | `{width: number, height: number}` | Mini、Dock | `{resized: true}` |
| `window.drag` | `{}` | 全部 | `{dragging: true}`，表示已安排 Windows 原生移动操作。 |
| `launch.choose` | `{}` | Workspace | `{cancelled: boolean, target: LaunchTarget\|null}` |
| `launch.execute` | `{targetId: string}` | 全部 | `LaunchResult` |
| `app.exit` | `{}` | 全部 | `{exiting: true}`，随后退出整个应用。 |

`app.getState.result.version` 是应用版本字符串 `"0.1.0"`，与外层通信版本整数 1 不同。`surface` 为 `mini`、`dock` 或 `workspace`；`effectiveTheme` 为当前生效的 `dark` 或 `light`；`settingsWarning` 为配置恢复警告或 `null`；`runtimeVersion` 为 WebView2 运行时版本或 `null`。

Workspace 页面名仅允许 `home`、`scenes`、`library`、`tags`、`favorites`、`search`、`settings`，字符串长度最多 24。`window.resizeSurface` 的宽度范围为 40–600，高度为 40–1200，值必须有限；它调整当前 Mini/Dock 原生窗口，再按工作区约束位置。网页不能用该方法改变 Workspace 大小。`window.drag` 不接受坐标或 HWND。

`focusRestore` 的当前结果包括 `not-attempted`、`external-user-focus-preserved`、`previous-window-unavailable`、`restored`、`windows-denied-activation`。Windows 可能拒绝前台激活；若用户已把焦点转到其他程序，宿主保留该焦点。关闭 Workspace 不会退出 Desktop Hub。

## 4. 可编辑设置与持久化配置

`settings.save` 是局部更新。宿主先复制当前设置，再合并以下字段，验证成功后持久化：

| 可由前端修改的字段 | 类型、取值与默认值 |
| --- | --- |
| `theme` | `system`、`dark`、`light`；默认 `system`。 |
| `reducedMotion` | boolean；默认 false。 |
| `transparency` | boolean；默认 true。 |
| `miniPosition`、`dockPosition` | `left`、`right`、`top`、`bottom`；默认 `left`。 |
| `miniSize`、`dockSize` | `small`、`normal`、`large`；默认 `normal`。 |
| `miniMode` | `icons`、`labels`；默认 `icons`。 |
| `hoverEnabled` | boolean；默认 true。 |
| `hoverDelay` | 整数毫秒；默认 500，保存时限制到 100–3000。 |
| `allowForeground` | boolean；默认 true。 |

界面不能通过 `settings.save` 写入 `schemaVersion`、窗口坐标或 `launchTarget`。其中 `launchTarget` 必须由 `launch.choose` 的原生文件选择器流程产生；窗口坐标由原生层采集并保存。未知设置字段返回 `FORBIDDEN_SETTING`。

完整配置使用 camelCase JSON，包括上述设置及：

| 原生管理字段 | 结构与默认值 |
| --- | --- |
| `schemaVersion` | 整数 1。 |
| `workspaceBounds` | `{left:100, top:80, width:1100, height:720}`。首次运行由宿主按当前工作区改为接近全屏。 |
| `miniBounds` | `{left:18, top:300, width:56, height:260}`。 |
| `dockBounds` | `{left:18, top:260, width:370, height:540}`。 |
| `launchTarget` | 默认 null，选择后为 `{id, path, displayName}`。id 为非空 GUID 的标准 D 格式，displayName 由程序文件名生成。 |

配置校验把窗口 left/top 限制到 ±32768；最大宽高为 7680×4320。Workspace 最小宽高为 480×320，Mini 为 40×40，Dock 为 200×200。非有限数值恢复默认对应值，空 bounds 恢复默认对象。实际窗口显示时还会受屏幕工作区限制，Workspace 显示最小值为 640×480。配置只记录几何数据，不记录前台窗口 HWND。

普通运行的数据目录为 `%LOCALAPPDATA%\DesktopHub`，配置文件为 `settings.json`；WebView2 用户数据为其 `WebView2` 子目录，诊断日志为 `host.log`。`--smoke-test` 使用应用输出目录内的 `smoke-data`，避免修改普通运行配置。

保存时先验证完整模型，再写入同目录的唯一 `settings.<GUID>.tmp`，使用 `WriteThrough` 并刷新至磁盘，最后通过 `File.Replace` 替换已有配置；首次保存通过 `File.Move` 生效。成功后才更新内存配置。发生写入失败时保留先前配置和内存状态，并尝试清理临时文件。

配置上限为 256 KiB，JSON 最大深度 16，未知持久化字段不接受。损坏 JSON、过大的文件、不支持的版本和无效设置恢复默认值，并尝试把原文件改名保留为 `settings.corrupt-<UTC 时间>-<GUID>.json`。备份失败和读写权限问题通过警告或错误返回；前端可从 `app.getState.settingsWarning` 展示恢复情况。程序后来丢失不会自动清除已保存的目标引用，下一次启动会返回 `target_missing`。

## 5. 程序选择与真实启动

`launch.choose` 打开单选、检查文件存在的 Windows `.exe` 文件选择器。用户取消返回 `cancelled: true` 和 `target: null`，不会清除原先目标。选择成功后，C# 校验路径及 PE 文件头，生成新 GUID，保存目标并发布 `state.changed`。

`launch.execute` 只接收 1–96 个字符的 `targetId`，不接受 `path`、`arguments`、`workingDirectory`、`command`、`verb` 或脚本正文。原生层确认其为非空 GUID 且匹配当前已存储目标，然后重新检查实际文件，通过 `Process.Start` 创建进程。工作目录固定为该程序所在目录，`UseShellExecute=false`，没有启动参数，也没有 `runas` 或自动管理员权限申请。

当前路径约束包括本地盘符绝对路径、`.exe` 扩展名、拒绝 UNC 和设备前缀、相对路径、协议、备用数据流、网络映射磁盘及常见 DOS 设备名。PE 检查确认 DOS/PE 签名及基本文件头位置；Windows 仍会决定二进制是否可以运行。

按文件名拒绝的宿主包括 `powershell`、`powershell_ise`、`pwsh`、`cmd`、`wscript`、`cscript`、`mshta`、`rundll32`、`regsvr32`、`dotnet`、`python`、`pythonw`、`python3`、带版本号的 Python、`node`、`nodejs`、`bash`、`sh`、`zsh`、`wsl`、`git-bash`、`csi`、`fsi`、`perl`、`ruby`，名称比较忽略大小写。

这些约束限制网页对系统的调用入口。它们不是对用户选中 EXE 的代码签名策略或进程沙箱，不能全面识别改名及自定义解释器，也不能限制该 EXE 内部的行为。本轮不提供脚本动作、终端命令或任意字符串执行接口。

成功的 `LaunchResult` 为 `{success:true, processId:<PID>, code:"started", message:<说明>}`，表示 Windows 已创建进程；不宣称程序已完成初始化，也不等待窗口就绪。失败为 `{success:false, processId:null, code, message}`，按真实校验或系统异常返回。

| 启动结果 code | 含义 |
| --- | --- |
| `started` | Windows 返回已创建进程的 PID。 |
| `target_not_selected` | 尚未选择程序。 |
| `target_not_registered` | 请求 id 无效或不匹配已存储目标。 |
| `invalid_target` | 路径格式、设备路径或网络目标不允许。 |
| `unsupported_target` | 不是普通 `.exe`。 |
| `blocked_host` | 已知命令或解释器宿主被拒绝。 |
| `target_missing` | 文件或目录不存在，或 Windows 返回错误 2/3。 |
| `invalid_executable` | PE 校验失败，或 Windows 返回错误 193/216。 |
| `access_denied` | 文件访问被拒绝，或 Windows 返回错误 5。 |
| `elevation_required` | Windows 返回错误 740；应用不会主动提权。 |
| `io_error` | 读取程序时发生 I/O 错误。 |
| `launch_failed` | 其他实际创建进程失败或无法读取进程结果。 |

选择阶段的目标校验错误通过 `ok:false/error` 返回；执行阶段的预期启动错误通过 `ok:true/result.success:false` 返回。界面必须检查这两个层次，不能把收到通信响应等同于启动成功。

## 6. 通信错误与扩展约束

| 通信 error.code | 含义 |
| --- | --- |
| `MESSAGE_TOO_LARGE` | 消息文本超过限制。 |
| `INVALID_REQUEST` | 请求编号、类型或基本结构不合法。 |
| `UNSUPPORTED_VERSION` | 协议版本不是 1。 |
| `INVALID_PARAMS` | JSON、参数字段、值或设置校验失败。 |
| `BUSY` | 请求 id 重复、并发超限或选择/启动操作尚未结束。 |
| `WRONG_SURFACE` | 当前窗口没有调用该方法的权限。 |
| `FORBIDDEN_SETTING` | 界面尝试修改原生管理或未开放的设置。 |
| `UNKNOWN_MESSAGE` | 方法不在白名单内。 |
| `NATIVE_ERROR` | 未单独分类的原生异常，例如读取必需参数时发生异常。 |

前端包装还可能自行返回 `HOST_UNAVAILABLE`、`INVALID_PAYLOAD`、`SEND_FAILED`、`TIMEOUT`。这些不是宿主发送的启动结果。

后续扩展应增加独立方法、参数验证、窗口权限和明确返回模型，保留请求 id 关联及版本校验。新增动作类型需要独立实现和验收；不得添加接受任意命令字符串的通用 `system.exec`，也不得借设置保存接口开放启动路径或参数。
