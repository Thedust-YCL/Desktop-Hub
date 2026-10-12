using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopHub.Windows;

/// <summary>Current-user, session and profile scoped instance gate. IPC accepts one fixed activation request only.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private static readonly byte[] Request = Encoding.ASCII.GetBytes("WORKSPACE\n");
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly bool allowForegroundGrant;
    private readonly CancellationTokenSource stopping = new();
    private Task? listener;
    private bool disposed;
    public bool IsPrimaryInstance { get; }
    public string? LastError { get; private set; }
    public uint? LastServerProcessId { get; private set; }
    internal string DiagnosticPipeName => pipeName;

    public SingleInstanceService(string profileIdentity, bool allowForegroundGrant = true)
    {
        this.allowForegroundGrant = allowForegroundGrant;
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (string.IsNullOrWhiteSpace(profileIdentity)) throw new ArgumentException("A stable profile identity is required.", nameof(profileIdentity));
        // The caller supplies a stable profile identity; never reinterpret a label relative to the process cwd.
        var identity = $"{user.User?.Value}|{process.SessionId}|{profileIdentity.Trim().ToUpperInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        pipeName = "DesktopHub.M23." + hash;
        // Creation is atomic. Keeping the handle open marks the primary instance without thread-owned ReleaseMutex.
        mutex = new Mutex(false, @"Local\" + pipeName, out var created);
        IsPrimaryInstance = created;
    }

    public void StartListening(Action activateWorkspace)
    {
        if (disposed) throw new ObjectDisposedException(nameof(SingleInstanceService));
        if (!IsPrimaryInstance) throw new InvalidOperationException("Only the primary instance may listen.");
        if (listener is not null) return;
        listener = ListenAsync(activateWorkspace, stopping.Token);
    }

    private async Task ListenAsync(Action activateWorkspace, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64, 64);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                var input = new byte[Request.Length];
                var offset = 0;
                while (offset < input.Length)
                {
                    var received = await server.ReadAsync(input.AsMemory(offset), requestTimeout.Token).ConfigureAwait(false);
                    if (received == 0) break;
                    offset += received;
                }
                if (offset == Request.Length && server.IsMessageComplete && input.AsSpan().SequenceEqual(Request))
                {
                    // The callback is responsible for dispatching onto the WPF UI thread.
                    activateWorkspace();
                    await server.WriteAsync(new byte[] { 1 }, requestTimeout.Token).ConfigureAwait(false);
                    await server.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LastError = error.Message;
                // Avoid a busy loop if another process pre-created the endpoint or the OS rejects access.
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task<bool> NotifyExistingInstanceAsync(CancellationToken cancellationToken = default)
    {
        if (IsPrimaryInstance || disposed) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
            {
                LastServerProcessId = serverProcessId;
                // Grant only to the peer authenticated by CurrentUserOnly. Windows still decides whether the caller may grant focus.
                if (allowForegroundGrant) AllowSetForegroundWindow(serverProcessId);
            }
            await client.WriteAsync(Request, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var reply = new byte[1];
            return await client.ReadAsync(reply, timeout.Token).ConfigureAwait(false) == 1 && reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        { LastError = error.Message; return false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        // Listener cancellation never waits for the dispatcher callback, so teardown cannot deadlock the UI.
        if (listener is not null) _ = listener.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default);
        else stopping.Dispose();
        mutex.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
