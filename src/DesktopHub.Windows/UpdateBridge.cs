using System.Text.Json;
using System.Windows;

namespace DesktopHub.Windows;

internal sealed class UpdateBridge(HubApplication host, HubWindow window)
{
    public object Dispatch(string type, JsonElement payload)
    {
        if (window.Surface != "workspace") throw new BridgeException("WRONG_SURFACE", "请在工作空间检查更新。");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any())
            throw new BridgeException("INVALID_PARAMS", "更新操作不接受额外参数。");
        if (host.Smoke) return host.Updates.Snapshot();
        switch (type)
        {
            case "update.getState": break;
            case "update.check": _ = host.Updates.CheckAsync(); break;
            case "update.download": _ = host.Updates.DownloadAsync(); break;
            case "update.install":
                if (host.IsBusyForUpdate(window)) throw new BridgeException("BUSY", "请等待场景、布局恢复和其他操作完成后再更新。");
                if (!host.Updates.TryPrepareInstall(false)) throw new BridgeException("UPDATE_NOT_READY", "更新尚未就绪，请检查下载状态后重试。");
                // Reply first; updater waits for normal shutdown, preserving the established cleanup path.
                _ = Application.Current.Dispatcher.BeginInvoke(host.Exit);
                break;
            default: throw new BridgeException("UNKNOWN_MESSAGE", "不支持此更新操作。");
        }
        return host.Updates.Snapshot();
    }
}
