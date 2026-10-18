using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopHub.Core;

namespace DesktopHub.Windows;

internal static class UiLocalization
{
    private static readonly HashSet<string> MessageFields = ["message", "error", "warning", "settingsWarning", "recoveryWarning"];

    public static string Serialize(object payload, string language)
    {
        var node = JsonSerializer.SerializeToNode(payload, HubApplication.Json);
        void Visit(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                foreach (var (key, value) in obj.ToArray())
                {
                    if (MessageFields.Contains(key) && value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[key] = UiText.Translate(language, text);
                    else if (key == "warnings" && value is JsonArray warnings)
                    {
                        for (var index = 0; index < warnings.Count; index++)
                            if (warnings[index] is JsonValue warning && warning.TryGetValue<string>(out var warningText)) warnings[index] = UiText.Translate(language, warningText);
                    }
                    else Visit(value);
                }
            }
            else if (current is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(node);
        return node?.ToJsonString(HubApplication.Json) ?? "null";
    }
}
