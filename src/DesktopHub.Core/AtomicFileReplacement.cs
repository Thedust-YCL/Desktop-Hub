namespace DesktopHub.Core;

internal static class AtomicFileReplacement
{
    internal static void Replace(string replacement, string destination) =>
        Commit(() => File.Replace(replacement, destination, null), Thread.Sleep);

    internal static void Commit(Action replace, Action<int> wait)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { replace(); return; }
            catch (IOException error) when (error.HResult == unchecked((int)0x80070497) && attempt < 2)
            {
                // Win32 ERROR_UNABLE_TO_REMOVE_REPLACED retains both original names.
                // Retry the same atomic operation only; other errors preserve their failure.
                wait(20 * (attempt + 1));
            }
        }
    }
}
