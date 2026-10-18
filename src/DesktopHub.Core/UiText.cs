using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopHub.Core;

/// <summary>Localizes trusted interface messages. Captured names and paths stay unchanged.</summary>
public static class UiText
{
    private static readonly Lazy<Dictionary<string, string>> Catalog = new(() =>
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("DesktopHub.Core.Translations.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    });
    private static readonly Lazy<(Regex Pattern, string[] Keys, string Target)[]> Patterns = new(() =>
        Catalog.Value.Where(pair => pair.Key.Contains('{')).Select(pair =>
        {
            var matches = Regex.Matches(pair.Key, @"\{[^{}]+\}");
            var pattern = "^";
            var offset = 0;
            foreach (Match match in matches)
            {
                pattern += Regex.Escape(pair.Key[offset..match.Index]) + "(.*?)";
                offset = match.Index + match.Length;
            }
            pattern += Regex.Escape(pair.Key[offset..]) + "$";
            return (new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(25)),
                matches.Select(match => match.Value).ToArray(), pair.Value);
        }).ToArray());

    public static string Translate(string? language, string value)
    {
        if (language != "en-US")
            return Catalog.Value.ContainsKey(value) ? value.Replace("Desktop Hub", "桌面工作台", StringComparison.Ordinal) : value;
        if (Catalog.Value.TryGetValue(value, out var translated)) return translated;
        if (value.Length > 16384) return value;
        foreach (var (pattern, keys, target) in Patterns.Value)
        {
            Match match;
            try { match = pattern.Match(value); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            var result = target;
            for (var index = 0; index < keys.Length; index++)
            {
                var captured = match.Groups[index + 1].Value;
                // This placeholder is a source-owned process/window selector, not user text.
                if (keys[index].Contains("condition ==", StringComparison.Ordinal) || keys[index].Contains(".Message", StringComparison.Ordinal)) captured = Translate(language, captured);
                result = result.Replace(keys[index], captured, StringComparison.Ordinal);
            }
            return result;
        }
        // Scene outcomes compose a small number of source-owned semicolon clauses.
        // Split only when the complete clause is catalogued; names and paths remain opaque.
        for (var index = value.IndexOf('；'); index >= 0; index = value.IndexOf('；', index + 1))
        {
            var leading = value[..(index + 1)];
            var trailing = value[index..];
            if (Catalog.Value.TryGetValue(leading, out var prefixText)) return prefixText + Translate(language, value[(index + 1)..]);
            if (Catalog.Value.TryGetValue(trailing, out var suffixText)) return Translate(language, value[..index]) + suffixText;
        }
        // Native validators sometimes concatenate a field identifier after a colon.
        var prefix = Catalog.Value.Keys.Where(key => key.EndsWith('：') && value.StartsWith(key, StringComparison.Ordinal)).OrderByDescending(key => key.Length).FirstOrDefault();
        return prefix is null ? value : Catalog.Value[prefix] + Translate(language, value[prefix.Length..]);
    }
}
