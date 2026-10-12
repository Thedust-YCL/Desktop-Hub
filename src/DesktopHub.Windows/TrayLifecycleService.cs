using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace DesktopHub.Windows;

/// <summary>A single managed NotifyIcon and a hidden broadcast receiver for Explorer taskbar reconstruction.</summary>
public sealed class TrayLifecycleService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem miniItem;
    private readonly Func<bool> isMiniVisible;
    private readonly HwndSource broadcastWindow;
    private readonly int taskbarCreated;
    private bool disposed;
    public int RecoveryCount { get; private set; }
    internal IntPtr DiagnosticWindowHandle => broadcastWindow.Handle;
    internal int DiagnosticTaskbarCreatedMessage => taskbarCreated;

    public TrayLifecycleService(Action openWorkspace, Func<bool> isMiniVisible, Action toggleMini,
        Action openSettings, Action showDesktopIcons, Action exit)
    {
        this.isMiniVisible = isMiniVisible;
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 Workspace", null, (_, _) => openWorkspace());
        miniItem = new Forms.ToolStripMenuItem("显示 Mini", null, (_, _) => { toggleMini(); RefreshMiniLabel(); });
        menu.Items.Add(miniItem);
        menu.Items.Add("打开设置", null, (_, _) => openSettings());
        menu.Items.Add("显示 / 恢复桌面图标", null, (_, _) => showDesktopIcons());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("完整退出 Desktop Hub", null, (_, _) => exit());
        menu.Opening += (_, _) => RefreshMiniLabel();
        icon = new Forms.NotifyIcon { Icon = SystemIcons.Application, Text = "Desktop Hub", ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => openWorkspace();
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // Broadcasts do not reach HWND_MESSAGE, so this receiver must be an invisible top-level tool window.
        broadcastWindow = new HwndSource(new HwndSourceParameters("DesktopHub.TrayRecovery")
        { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = 0x80 });
        broadcastWindow.AddHook(WindowProc);
        RefreshMiniLabel();
        icon.Visible = true;
    }

    public void RefreshMiniLabel()
    {
        if (!disposed) miniItem.Text = isMiniVisible() ? "隐藏 Mini" : "显示 Mini";
    }
    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!disposed && taskbarCreated != 0 && message == taskbarCreated)
        {
            // NotifyIcon also supports this message internally. Removing and adding the same managed ID remains idempotent.
            icon.Visible = false;
            icon.Visible = true;
            RecoveryCount++;
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        broadcastWindow.RemoveHook(WindowProc);
        broadcastWindow.Dispose();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string message);
}
