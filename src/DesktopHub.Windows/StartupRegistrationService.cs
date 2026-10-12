using System.Security;
using System.IO;
using DesktopHub.Core;
using Microsoft.Win32;

namespace DesktopHub.Windows;

public sealed class StartupRegistrationService : StartupRegistrationController
{
    public StartupRegistrationService(SettingsStore store, IStartupRegistrationBackend? backend = null, string? executablePath = null)
        : base(store, backend ?? new CurrentUserRunBackend(), executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前程序路径。")) { }
}

/// <summary>HKCU only; default reads never create a key. Never delete an entire Run key.</summary>
public sealed class CurrentUserRunBackend : IStartupRegistrationBackend
{
    public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "DesktopHub";
    public StartupBackendResult Query()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return new(true, null);
            if (value is not string command || key!.GetValueKind(ValueName) != RegistryValueKind.String)
                return new(false, null, "startup_entry_unsupported", "同名启动项不是普通字符串，未改动。请在注册表/任务管理器中核对。");
            return new(true, command);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { return new(false, null, "registry_unavailable", "当前用户启动项读取失败：" + ex.Message); }
    }
    public StartupBackendResult CompareExchange(string? expectedCommand, string? replacementCommand)
    {
        try
        {
            if (replacementCommand is { Length: > 260 }) return new(false, null, "startup_path_too_long", "启动命令超过 Windows Run 的 260 字符上限。");
            // Windows Registry has no public compare/exchange primitive. Check exact value immediately before
            // this single-value mutation; concurrent external registry edits remain a documented limitation.
            var current = Query();
            if (!current.Success || current.Command != expectedCommand)
                return current with { Success = false, Code = "startup_entry_changed", Message = "启动项被外部程序改变，操作已取消。" };
            if (current.Command == replacementCommand) return current;
            using var key = replacementCommand is null
                ? Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
                : Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key is null && replacementCommand is not null) return new(false, current.Command, "registry_unavailable", "无法打开当前用户 Run 注册位置。");
            var actual = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (!Equals(actual, expectedCommand)) return new(false, actual as string, "startup_entry_changed", "启动项在写入前发生变化，操作已取消。");
            if (replacementCommand is null) key?.DeleteValue(ValueName, throwOnMissingValue: false);
            else key!.SetValue(ValueName, replacementCommand, RegistryValueKind.String);
            key?.Flush();
            return Query();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { return new(false, null, "registry_unavailable", "当前用户启动项变更失败：" + ex.Message); }
    }
}
