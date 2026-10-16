using System.IO;
using DesktopHub.Core;

namespace DesktopHub.Windows;

internal sealed record PreviewResult(byte[] Png, int Width, int Height, string Kind, string Detail);

internal sealed class ResourcePreviewService : IDisposable
{
    internal const long DecodedBudget = 16 * 1024 * 1024, CacheBudget = 32 * 1024 * 1024;
    // Shared across services/windows. A cancelled synchronous decoder still occupies its slot until terminal.
    private static readonly SemaphoreSlim Decoders = new(2, 2);
    private readonly ResourceLibraryService library;
    private readonly object gate = new();
    private readonly Dictionary<(object Owner, string Id), CancellationTokenSource> requests = [];
    private readonly Dictionary<string, LinkedListNode<(string Key, PreviewResult Value)>> cache = [];
    private readonly LinkedList<(string Key, PreviewResult Value)> lru = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<Stream, string, bool, CancellationToken, Task<PreviewResult>> decode;
    private long cacheBytes, generation;
    private bool disposed;
    internal long CacheBytes { get { lock (gate) return cacheBytes; } }
    internal ResourcePreviewService(ResourceLibraryService library) : this(library, PreviewDecoder.RenderAsync) { }
    internal ResourcePreviewService(ResourceLibraryService library, Func<Stream, string, bool, CancellationToken, Task<PreviewResult>> decode)
    { this.library = library; this.decode = decode; library.Changed += Invalidate; }
    internal async Task<PreviewResult> StartRenderAsync(object owner, string requestId, string resourceId, bool thumbnail, CancellationToken token = default)
    {
        PreviewRequestParser.ValidateId(requestId); CancellationTokenSource source;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (requests.ContainsKey((owner, requestId))) throw new InvalidOperationException("预览请求编号正在使用。");
            source = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); requests.Add((owner, requestId), source);
        }
        try { return await RenderAsync(resourceId, thumbnail, source.Token); }
        finally { lock (gate) { requests.Remove((owner, requestId)); source.Dispose(); } }
    }
    internal void Cancel(object owner, string requestId) { lock (gate) if (requests.TryGetValue((owner, requestId), out var source)) source.Cancel(); }
    internal void Cancel(string requestId) { lock (gate) foreach (var pair in requests.Where(pair => pair.Key.Id == requestId)) pair.Value.Cancel(); }
    internal void CancelOwner(object owner) { lock (gate) foreach (var pair in requests.Where(pair => ReferenceEquals(pair.Key.Owner, owner))) pair.Value.Cancel(); }
    internal void Invalidate()
    { lock (gate) { ++generation; cache.Clear(); lru.Clear(); cacheBytes = 0; foreach (var source in requests.Values) source.Cancel(); } }
    internal Task<PreviewResult> RenderAsync(string resourceId, bool thumbnail, CancellationToken token)
        => Task.Run(() => RenderCoreAsync(resourceId, thumbnail, token));
    private async Task<PreviewResult> RenderCoreAsync(string resourceId, bool thumbnail, CancellationToken token)
    {
        PreviewRequestParser.ValidateId(resourceId); token.ThrowIfCancellationRequested();
        long revision; lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); revision = generation; }
        await Decoders.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var authorization = Authorize(resourceId);
            using var file = NativePreviewFile.Open(authorization);
            var key = $"preview-v2-cpu-raster-warp-pdf|{resourceId}|{authorization.CanonicalPath.ToUpperInvariant()}|{file.Stamp}|{thumbnail}|page0/frame0";
            PreviewResult? result = null;
            lock (gate) if (thumbnail && cache.TryGetValue(key, out var node)) { lru.Remove(node); lru.AddFirst(node); result = node.Value.Value; }
            result ??= await decode(file.Stream, authorization.Extension, thumbnail, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); file.Verify();
            if (Authorize(resourceId) != authorization) throw new IOException("资源授权已变化，请重新预览。");
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (revision != generation || disposed) throw new OperationCanceledException("预览已失效。", token);
                if (result.Png.LongLength > DecodedBudget || result.Width <= 0 || result.Height <= 0 || Math.Max(result.Width, result.Height) > (authorization.Extension == ".pdf" ? 1200 : 1600)) throw new IOException("预览超出尺寸预算。");
                if (thumbnail && !cache.ContainsKey(key))
                {
                    while (cacheBytes + result.Png.LongLength > CacheBudget && lru.Last is { } last) { cacheBytes -= last.Value.Value.Png.LongLength; cache.Remove(last.Value.Key); lru.RemoveLast(); }
                    var node = lru.AddFirst((key, result)); cache.Add(key, node); cacheBytes += result.Png.LongLength;
                }
            }
            return result;
        }
        finally { Decoders.Release(); }
    }
    private Resource Authorize(string id)
    {
        var data = library.Store.Snapshot();
        var resource = data.Resources.SingleOrDefault(item => item.Id == id) ?? throw new ArgumentException("资源不存在，请刷新。");
        if (resource.Status != "Active" || resource.ResourceType != "file") throw new IOException("此资源不可预览，请刷新或重新关联。");
        if (!resource.ManualTarget && !data.ResourceSources.Any(link => link.ResourceId == id && data.Sources.Any(source => source.Id == link.SourceRootId && source.AvailabilityStatus == "Active" && ResourceScanner.Within(resource.CanonicalPath, source.Path)))) throw new IOException("资源来源授权已失效。");
        if (resource.CloudPlaceholder || resource.ReparsePoint) throw new IOException("云占位或链接不读取内容，请先在 Windows 中检查。");
        // Store authorization remains separate from live path/handle verification.
        return resource;
    }
    public void Dispose()
    { lock (gate) { if (disposed) return; disposed = true; library.Changed -= Invalidate; ++generation; lifetime.Cancel(); foreach (var source in requests.Values) source.Cancel(); cache.Clear(); lru.Clear(); cacheBytes = 0; } }
}
