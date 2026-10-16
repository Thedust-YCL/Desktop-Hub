using DesktopHub.Windows;
using System.Text.Json;
using DesktopHub.Core;

internal static class Task4BoundaryChecks
{
    internal static void Run(Action<string, Action> check)
    {
        object Parse(string method, string json)
        {
            using var doc = JsonDocument.Parse(json);
            return method == "Scan" ? ApplicationRequestParser.Scan(doc.RootElement) : ApplicationRequestParser.Search(doc.RootElement);
        }
        check("Task4 native scan boundary rejects frontend paths and invalid source/token combinations", () =>
        {
            Parse("Scan", "{\"source\":\"startMenu\",\"recursive\":true,\"scanId\":\"\"}");
            var token = Guid.NewGuid().ToString("N");
            Parse("Scan", $$"""{"source":"drop","recursive":false,"scanId":"{{token}}"}""");
            Parse("Scan", "{\"source\":\"files\",\"recursive\":false,\"scanId\":\"\"}");
            foreach (var json in new[] { "{\"source\":\"drop\",\"recursive\":true,\"scanId\":\"abc\"}", "{\"source\":\"files\",\"recursive\":false,\"scanId\":\"\",\"paths\":[\"C:/x.exe\"]}", "{\"source\":\"desktop\",\"recursive\":false,\"scanId\":\"forged\"}", "{\"source\":\"files\",\"source\":\"folder\",\"recursive\":false,\"scanId\":\"\"}" })
            { try { Parse("Scan", json); throw new Exception("Accepted unsafe scan payload"); } catch (ArgumentException) { } }
            foreach (var json in new[] { $$"""{"source":"drop","recursive":true,"scanId":"{{token}}"}""", "{\"source\":\"startMenu\",\"recursive\":false,\"scanId\":\"\"}", "{\"source\":\"files\",\"recursive\":true,\"scanId\":\"\"}" })
            { try { Parse("Scan", json); throw new Exception("Accepted invalid recursion"); } catch (ArgumentException) { } }
        });
        check("Task4 favorite-search filters corpus before cap fifty and rejects malformed boolean", () =>
        {
            var request = (ApplicationSearchRequest)Parse("Search", "{\"query\":\"needle\",\"type\":\"all\",\"favoritesOnly\":true}");
            var rows = Enumerable.Range(0, 60).Select(i => new SearchEntity("app", i.ToString(), "needle", "", "Active", false)).Append(new("file", "favorite", "zneedle", "", "Active", true));
            var result = request.Execute(rows);
            if (result.Count != 1 || result[0].Id != "favorite") throw new Exception("Favorite filtered after cap");
            var ordinary = (ApplicationSearchRequest)Parse("Search", "{\"query\":\"needle\",\"type\":\"all\",\"favoritesOnly\":false}");
            if (ordinary.Execute(rows).Count != 50 || ((ApplicationSearchRequest)Parse("Search", "{\"query\":\"needle\",\"type\":\"all\"}")).Execute(rows).Count != 50) throw new Exception("Omitted/false search changed behavior");
            foreach (var type in new[] { "app", "scene", "file", "folder", "tag", "project" })
            { var typed = (ApplicationSearchRequest)Parse("Search", $$"""{"query":"needle","type":"{{type}}","favoritesOnly":true}"""); if (typed.Execute(rows).Count != (type == "file" ? 1 : 0)) throw new Exception("Favorite type filter widened"); }
            foreach (var json in new[] { "{\"query\":\"\",\"type\":\"all\",\"favoritesOnly\":\"false\"}", "{\"query\":\"\",\"type\":\"bad\",\"favoritesOnly\":true}", "{\"query\":\"\",\"type\":\"all\",\"favoritesOnly\":false,\"favoritesOnly\":true}" })
            { try { Parse("Search", json); throw new Exception("Accepted malformed search"); } catch (ArgumentException) { } }
        });
        check("Task4 editor and group boundary accepts only IDs and validates independent Favorite atomically", () =>
        {
            void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected malformed request rejection"); }
            JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
            var id = Guid.NewGuid().ToString("D");
            var old = $$"""{"appId":"{{id}}","name":"App","arguments":"","workingDirectory":"","pinnedToMini":false,"pinnedToDock":true}""";
            if (ApplicationRequestParser.Update(Json(old)).Favorite is not null) throw new Exception("Omitted Favorite overwritten");
            foreach (var favorite in new[] { "true", "false" })
                if (ApplicationRequestParser.Update(Json(old[..^1] + ",\"favorite\":" + favorite + "}")).Favorite != (favorite == "true")) throw new Exception("Favorite parser conflated pins");
            Reject(() => ApplicationRequestParser.Update(Json(old[..^1] + ",\"favorite\":null}")));
            Reject(() => ApplicationRequestParser.Update(Json(old[..^1] + ",\"target\":\"C:/x.exe\"}")));
            var favoriteRequest = ApplicationRequestParser.Favorite(Json($$"""{"appId":"{{id}}","favorite":false}"""));
            if (favoriteRequest.AppId != id || favoriteRequest.Favorite) throw new Exception("Favorite request mismatch");
            if (ApplicationRequestParser.GroupSave(Json("{\"groupId\":null,\"name\":\"Work\"}")).GroupId is not null) throw new Exception("Cannot create group");
            if (ApplicationRequestParser.MoveGroup(Json($$"""{"appId":"{{id}}","groupId":null}""")).GroupId is not null) throw new Exception("Cannot ungroup app");
            if (ApplicationRequestParser.GroupRemove(Json($$"""{"groupId":"{{id}}"}""")) != id) throw new Exception("Remove ID changed");
            if (ApplicationRequestParser.GroupReorder(Json($$"""{"groupIds":["{{id}}"]}""")).Single() != id) throw new Exception("Group order changed");
            Reject(() => ApplicationRequestParser.GroupReorder(Json($$"""{"groupIds":["{{id}}","{{id}}"]}""")));
            Reject(() => ApplicationRequestParser.GroupSave(Json("{\"groupId\":null,\"name\":\"Work\",\"name\":\"Other\"}")));
            Reject(() => ApplicationRequestParser.MoveGroup(Json("{\"appId\":\"C:/x.exe\",\"groupId\":null}")));
            Reject(() => ApplicationRequestParser.Favorite(Json($$"""{"appId":"{{id}}","favorite":"true"}""")));
        });
        check("Task4 Dock collapse policy requires successful independent Dock request and enabled preference", () =>
        {
            foreach (var surface in new[] { "workspace", "mini", "dock" }) foreach (var enabled in new[] { false, true }) foreach (var success in new[] { false, true })
                if (ApplicationRequestParser.ShouldCloseDock(surface, enabled, success) != (surface == "dock" && enabled && success)) throw new Exception("Wrong Dock policy");
        });
    }
}
