using System.Globalization;
using System.Text.Json.Serialization;

namespace DesktopHub.Core;

public sealed record Project
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Description { get; set; } = "";
    [JsonRequired] public int DisplayOrder { get; set; }
    [JsonRequired] public bool PinnedToHome { get; set; }
    [JsonRequired] public List<ProjectItem> Items { get; set; } = [];
}
public sealed record ProjectItem
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("D");
    [JsonRequired] public string Type { get; set; } = "app";
    [JsonRequired] public string? EntityId { get; set; }
    [JsonRequired] public string? Url { get; set; }
    [JsonRequired] public string? Name { get; set; }
}
public sealed record ProjectFile
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public List<Project> Projects { get; set; } = [];
}

/// <summary>One syntax validator shared by project saves, file loading and host URL submission.</summary>
public static class ProjectUrlValidator
{
    public static string Normalize(string? value)
    {
        if (value is null || value.Any(char.IsControl)) throw new SettingsValidationException("网页地址无效。");
        var text = value.Trim();
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) throw new SettingsValidationException("网页地址必须明确指定 HTTP 或 HTTPS 主机。");
        var authority = text[(separator + 3)..].Split('/', '?', '#')[0];
        if (authority.EndsWith(':') || authority.Contains('@')) throw new SettingsValidationException("网页主机、端口或登录信息无效。");
        if (text.Length is 0 or > 1024 || text.Any(char.IsWhiteSpace) || text.Contains('\\') ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrEmpty(uri.Host) || Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown)
            throw new SettingsValidationException("请输入带有效主机的 HTTP 或 HTTPS 网页地址，不能包含登录信息。");
        if (uri.HostNameType == UriHostNameType.Dns)
        {
            string ascii;
            try { ascii = new IdnMapping().GetAscii(uri.Host.TrimEnd('.')); }
            catch (ArgumentException) { throw new SettingsValidationException("网页主机名无效。"); }
            if (ascii.Length is 0 or > 253 || ascii.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw new SettingsValidationException("网页主机名无效。");
        }
        var normalized = uri.AbsoluteUri;
        if (normalized.Length > 1024) throw new SettingsValidationException("规范化网页地址超过 1024 字符。");
        return normalized;
    }
}

/// <summary>Independent local projects contain ordered references, never copied launch targets.</summary>
public sealed class ProjectService
{
    private readonly object _gate = new();
    private readonly Func<string, string, bool> _entityExists;
    private readonly SchemaOneStore<ProjectFile> _store;
    public string? RecoveryWarning { get { lock (_gate) return _store.RecoveryWarning; } }
    public event Action? Changed;

    public ProjectService(string directory, Func<string, string, bool> entityExists)
    {
        ArgumentNullException.ThrowIfNull(entityExists);
        _entityExists = entityExists;
        // Shape validation at load deliberately does not consult transient entity availability.
        _store = new(directory, "projects.json", NormalizeFile);
    }

    public Project Save(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project committed;
        lock (_gate)
        {
            _store.EnsureWritable();
            var normalized = NormalizeProject(project);
            var prior = _store.Current.Projects.FirstOrDefault(p => p.Id == normalized.Id);
            foreach (var item in normalized.Items.Where(item => item.Type != "web"))
            {
                var unchanged = prior?.Items.Any(old => old.Id == item.Id && old.Type == item.Type && old.EntityId == item.EntityId) == true;
                if (!unchanged && !_entityExists(item.Type, item.EntityId!)) throw new SettingsValidationException("新增或更改的关联不存在，请重新选择当前已登记实体。");
            }
            var projects = _store.Current.Projects.Where(p => p.Id != normalized.Id).Select(Clone).ToList();
            if (projects.Count >= 200) throw new SettingsValidationException("项目最多 200 个。");
            projects.Insert(Math.Min(normalized.DisplayOrder, projects.Count), normalized);
            for (var i = 0; i < projects.Count; i++) projects[i].DisplayOrder = i;
            _store.Save(new() { Projects = projects });
            committed = Clone(_store.Current.Projects.Single(p => p.Id == normalized.Id));
        }
        Changed?.Invoke();
        return committed;
    }

    public void Remove(string id)
    {
        ActivityProjectIdentity.Stable(id);
        lock (_gate)
        {
            _store.EnsureWritable();
            var projects = _store.Current.Projects.Where(p => p.Id != id).Select(Clone).ToList();
            if (projects.Count == _store.Current.Projects.Count) throw new SettingsValidationException("项目已不存在。");
            for (var i = 0; i < projects.Count; i++) projects[i].DisplayOrder = i;
            _store.Save(new() { Projects = projects });
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<Project> Snapshot()
    {
        lock (_gate) return _store.Current.Projects.Select(Clone).ToList();
    }

    private static ProjectFile NormalizeFile(ProjectFile file)
    {
        if (file.SchemaVersion != 1 || file.Projects is null || file.Projects.Count > 200) throw new SettingsValidationException("项目文件无效。");
        var projects = file.Projects.Select(NormalizeProject).ToList();
        if (projects.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != projects.Count || projects.Where((p, index) => p.DisplayOrder != index).Any())
            throw new SettingsValidationException("项目 ID 或显示顺序重复、缺失。");
        return new() { Projects = projects };
    }

    private static Project NormalizeProject(Project project)
    {
        if (project is null) throw new SettingsValidationException("项目为空。");
        ActivityProjectIdentity.Stable(project.Id);
        if (project.Items is null || project.Items.Count > 100 || project.DisplayOrder is < 0 or > 199) throw new SettingsValidationException("项目顺序或关联数量无效。");
        var name = Scalar(project.Name, 80, "项目名称");
        if (project.Description is null || project.Description.Length > 500 || project.Description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new SettingsValidationException("项目说明最多 500 字符，不能包含特殊控制字符。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new HashSet<(string, string)>();
        var items = new List<ProjectItem>();
        foreach (var item in project.Items)
        {
            if (item is null) throw new SettingsValidationException("项目关联为空。");
            ActivityProjectIdentity.Stable(item.Id);
            if (!ids.Add(item.Id)) throw new SettingsValidationException("关联 ID 重复。");
            if (item.Type == "web")
            {
                if (item.EntityId is not null) throw new SettingsValidationException("网页不能包含实体 ID。");
                var url = ProjectUrlValidator.Normalize(item.Url);
                if (!references.Add(("web", url))) throw new SettingsValidationException("项目中网页重复。");
                items.Add(item with { Url = url, Name = item.Name is null ? null : Scalar(item.Name, 80, "网页名称") });
            }
            else
            {
                if (item.Type is not ("app" or "scene" or "file" or "folder") || item.Url is not null || item.Name is not null) throw new SettingsValidationException("项目关联类型或字段无效。");
                var entity = ActivityProjectIdentity.Entity(item.EntityId);
                if (!references.Add((item.Type, entity))) throw new SettingsValidationException("项目中实体关联重复。");
                items.Add(item with { });
            }
        }
        return project with { Name = name, Items = items };
    }

    private static string Scalar(string? value, int max, string label)
    {
        if (value is null || value.Any(char.IsControl) || value.Trim().Length is 0 || value.Trim().Length > max) throw new SettingsValidationException($"{label}须为 1–{max} 字符普通文本。");
        return value.Trim();
    }
    private static Project Clone(Project project) => project with { Items = project.Items.Select(item => item with { }).ToList() };
}
