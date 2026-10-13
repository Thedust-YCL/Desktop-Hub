using System.Globalization;
using System.Text;

namespace DesktopHub.Core;

public sealed record SearchEntity(string Type, string Id, string Name, string Detail, string Status, bool Favorite);

/// <summary>Searches registered metadata only, with bundled offline MIT Chinese readings.</summary>
public static class UnifiedSearchService
{
    private static readonly string[] Types = ["app", "scene", "file", "folder", "tag", "project"];
    private static readonly Lazy<Dictionary<int, string>> Initials = new(LoadInitials);
    public static IReadOnlyList<SearchEntity> Search(IEnumerable<SearchEntity> entities, string query, string type, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (query is null || query.Length > 200 || query.Any(char.IsControl)) throw new ArgumentException("搜索词无效。", nameof(query));
        if (type != "all" && !Types.Contains(type, StringComparer.Ordinal)) throw new ArgumentException("搜索类型无效。", nameof(type));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var text = query.Trim(); var initialsQuery = text.ToLowerInvariant();
        var matchInitials = text.Length > 0 && text.All(char.IsAsciiLetterOrDigit);
        return entities.Where(entity => type == "all" || entity.Type == type)
            .Select(entity => (Entity: entity, Rank: Rank(entity, text, initialsQuery, matchInitials)))
            .Where(item => item.Rank >= 0).OrderBy(item => item.Rank)
            .ThenBy(item => text.Length == 0 && item.Entity.Status != "Active" ? 1 : 0)
            .ThenBy(item => text.Length == 0 && item.Entity.Favorite ? 0 : 1)
            .ThenBy(item => Array.IndexOf(Types, item.Entity.Type))
            .ThenBy(item => item.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Entity.Detail, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Entity.Id, StringComparer.Ordinal)
            .Take(Math.Min(limit, 50)).Select(item => item.Entity).ToArray();
    }
    private static int Rank(SearchEntity entity, string query, string initialsQuery, bool matchInitials)
    {
        if (query.Length == 0 || entity.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entity.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (entity.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (matchInitials && MatchesInitials(entity.Name, initialsQuery)) return 3;
        return entity.Detail.Contains(query, StringComparison.OrdinalIgnoreCase) ? 4 : -1;
    }
    private static bool MatchesInitials(string name, string query)
    {
        // Sets are matched per rune: no Cartesian explosion for polyphonic names.
        var sets = name.EnumerateRunes().Select(rune => Initials.Value.TryGetValue(rune.Value, out var letters)
            ? letters : Rune.ToLowerInvariant(rune).ToString()).ToArray();
        for (var start = 0; start <= sets.Length - query.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < query.Length; i++) if (!sets[start + i].Contains(query[i])) { matched = false; break; }
            if (matched) return true;
        }
        return false;
    }
    private static Dictionary<int, string> LoadInitials()
    {
        using var stream = typeof(UnifiedSearchService).Assembly.GetManifestResourceStream("DesktopHub.PinyinData") ?? throw new InvalidOperationException("缺少本地拼音数据。");
        using var reader = new StreamReader(stream, Encoding.UTF8); var result = new Dictionary<int, string>();
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("U+", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':'); var comment = line.IndexOf('#');
            var code = int.Parse(line.AsSpan(2, colon - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var readings = line[(colon + 1)..(comment < 0 ? line.Length : comment)].Split(',');
            var initials = readings.Select(reading => reading.Trim().Normalize(NormalizationForm.FormD)[0])
                .Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).Distinct().Order().ToArray();
            if (initials.Length > 0) result.Add(code, new string(initials));
        }
        return result;
    }
}
