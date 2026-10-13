using System.Diagnostics;
using DesktopHub.Core;

internal static class UnifiedSearchChecks
{
    private static string[] Search((string Type, string Id, string Name, string Detail, string Status, bool Favorite)[] rows, string query, string type = "all", int limit = 50)
    {
        return UnifiedSearchService.Search(rows.Select(r => new SearchEntity(r.Type,r.Id,r.Name,r.Detail,r.Status,r.Favorite)),query,type,limit).Select(row=>row.Id).ToArray();
    }
    internal static void Run(Action<string, Action> check)
    {
        check("Unified search ranks exact, prefix, contains, initials and registered detail", () =>
        {
            (string, string, string, string, string, bool)[] rows = [("file","detail","别的","C:/alpha.txt","Active",false),("app","contains","my alpha","","Active",false),("scene","prefix","alpha plan","","Active",false),("tag","exact","ALPHA","","Active",false)];
            Expect(["exact","prefix","contains","detail"], Search(rows,"alpha"));
        });
        check("Unified search uses full rune pinyin and every polyphonic initial", () =>
        {
            (string, string, string, string, string, bool)[] rows = [("app","cq","重庆","","Active",false),("app","wx","微信","","Active",false),("file","rare","龘文𠀀","","Active",false),("file","mixed","微信Notes9","","Active",false)];
            Expect(["cq"],Search(rows,"cq")); Expect(["cq"],Search(rows,"zq")); Expect(["wx","mixed"],Search(rows,"wx")); Expect(["rare"],Search(rows,"dwh")); Expect(["mixed"],Search(rows,"wxnotes9"));
        });
        check("Unified search preserves stable identity and same-name type order independent of enumeration", () =>
        {
            (string, string, string, string, string, bool)[] rows = [("tag","t","同名","","Active",false),("file","z","同名","C:/a","Missing",false),("app","a","同名","","Active",false),("file","b","同名","C:/a","Active",false),("scene","s","同名","2个动作","Disabled",false)];
            Expect(["a","s","b","z","t"],Search(rows,"同名")); Expect(Search(rows,"同名"),Search(rows.Reverse().ToArray(),"同名")); Expect(["b","z"],Search(rows,"同名","file"));
        });
        check("Unified search caps fifty and bounds polyphonic work on ten thousand indexed rows", () =>
        {
            var rows = Enumerable.Range(0,10000).Select(i => ("file",i.ToString("D5"),"重行" + i,"C:/owned/记录" + i,"Active",false)).ToArray();
            if(Search(rows,"", "all",500).Length != 50 || Search(rows,"ch","all",7).Length != 7) throw new Exception("Search limit violated");
            var times = new List<double>();
            for(var i=0;i<35;i++) { var timer=Stopwatch.StartNew(); Search(rows,"ch"); timer.Stop(); if(i>=5) times.Add(timer.Elapsed.TotalMilliseconds); }
            times.Sort(); Console.WriteLine($"SEARCH 10000 rows / 30 warm queries p95={times[(int)Math.Ceiling(times.Count*.95)-1]:F2}ms (Core only; UI debounce/bridge excluded)");
        });
    }
    private static void Expect(string[] expected,string[] actual) { if(!expected.SequenceEqual(actual)) throw new Exception($"Expected [{string.Join(',',expected)}], got [{string.Join(',',actual)}]"); }
}
