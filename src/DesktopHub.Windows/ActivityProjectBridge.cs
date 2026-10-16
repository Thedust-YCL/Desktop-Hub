using System.Diagnostics;
using System.Text.Json;
using DesktopHub.Core;

namespace DesktopHub.Windows;

internal sealed class ActivityProjectBridge(HubApplication host, HubWindow window)
{
    internal async Task<object> DispatchAsync(string type, JsonElement payload)
    {
        if (window.Surface != "workspace") throw new BridgeException("WRONG_SURFACE", "请在工作空间查看最近使用和项目。");
        switch (type)
        {
            case "activity.query":
                ActivityProjectRequestParser.Empty(payload);
                var activityResources = (await host.GetResourcesAsync()).Store.Snapshot();
                var activitySettings = host.Store.Current;
                return new { items = host.Activity.Snapshot().Select(record => ActivityProjectMetadata.Activity(record, activitySettings, activityResources)).ToArray(), warning = host.Activity.RecoveryWarning };
            case "activity.clear":
                ActivityProjectRequestParser.Empty(payload);
                await Task.Run(host.Activity.Clear);
                return new { cleared = true };
            case "projects.query":
                var projectId = ActivityProjectRequestParser.Query(payload);
                var resources = (await host.GetResourcesAsync()).Store.Snapshot();
                var settings = host.Store.Current;
                var projects = host.Projects.Snapshot();
                var selected = projects.FirstOrDefault(project => project.Id == projectId);
                return new { projects = projects.Select(ActivityProjectMetadata.Summary).ToArray(), project = selected,
                    items = selected?.Items.Select(item => ActivityProjectMetadata.Item(item, settings, resources)).ToArray() ?? [], warning = host.Projects.RecoveryWarning };
            case "projects.save":
                var candidate = ActivityProjectRequestParser.Save(payload);
                await host.GetResourcesAsync();
                return new { project = await Task.Run(() => host.Projects.Save(candidate)) };
            case "projects.remove":
                var removeId = ActivityProjectRequestParser.Remove(payload);
                await Task.Run(() => host.Projects.Remove(removeId));
                return new { removed = true };
            case "projects.openWeb":
                var request = ActivityProjectRequestParser.OpenWeb(payload);
                var project = host.Projects.Snapshot().FirstOrDefault(item => item.Id == request.ProjectId) ?? throw new ArgumentException("项目已移除。");
                var web = project.Items.FirstOrDefault(item => item.Id == request.ItemId && item.Type == "web") ?? throw new ArgumentException("网页关联已移除或改变，请刷新项目。");
                var url = ProjectUrlValidator.Normalize(web.Url);
                await Task.Run(() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
                return new { submitted = true, message = "已向 Windows 提交网页打开请求。" };
            default: throw new BridgeException("UNKNOWN_MESSAGE", "不支持此最近使用或项目操作。");
        }
    }
}
