using DesktopHub.Core;

internal static class AtomicFileReplacementChecks
{
    public static void Run(Action<string, Action> check, string root)
    {
        const int transient = unchecked((int)0x80070497);
        (string Source, string Target) Files()
        {
            var directory = Path.Combine(root, "atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "replacement.tmp");
            var target = Path.Combine(directory, "settings.json");
            File.WriteAllText(source, "new"); File.WriteAllText(target, "old");
            return (source, target);
        }
        static void Require(bool condition) { if (!condition) throw new Exception("Atomic replacement contract failed"); }
        check("Atomic replacement recovers 1175 with bounded waits and keeps old bytes until commit", () =>
        {
            var files = Files(); var attempts = 0; var waits = new List<int>();
            AtomicFileReplacement.Commit(() =>
            {
                Require(File.ReadAllText(files.Target) == "old" && File.ReadAllText(files.Source) == "new");
                if (++attempts <= 2) throw new IOException("controlled Windows 1175", transient);
                File.Replace(files.Source, files.Target, null);
            }, waits.Add);
            Require(attempts == 3 && waits.SequenceEqual(new[] { 20, 40 }));
            Require(File.ReadAllText(files.Target) == "new" && !File.Exists(files.Source));
        });
        check("Persistent 1175 propagates after three attempts without changing original files", () =>
        {
            var files = Files(); var attempts = 0; var waits = 0;
            try { AtomicFileReplacement.Commit(() => { attempts++; throw new IOException("persistent", transient); }, _ => waits++); throw new Exception("Expected persistence failure"); }
            catch (IOException error) { Require(error.HResult == transient); }
            Require(attempts == 3 && waits == 2 && File.ReadAllText(files.Target) == "old" && File.ReadAllText(files.Source) == "new");
        });
        check("Atomic replacement never retries access sharing or ambiguous move errors", () =>
        {
            foreach (var code in new[] { 5, 32, 1176, 1177, 1234 })
            {
                var attempts = 0; var waits = 0; var hresult = unchecked((int)0x80070000) | code;
                try { AtomicFileReplacement.Commit(() => { attempts++; throw new IOException("non-retryable", hresult); }, _ => waits++); throw new Exception("Expected original error"); }
                catch (IOException error) { Require(error.HResult == hresult); }
                Require(attempts == 1 && waits == 0);
            }
        });
    }
}
