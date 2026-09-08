using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Player;

public sealed partial class MpvPlaybackPreviewService : ILocalPlaybackPreviewService
{
    private const int MaximumFrames = 96;
    private const int MaximumCacheBytes = 24 * 1024 * 1024;
    private readonly object requestLock = new();
    private readonly object cacheLock = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<IMpvPreviewDecoder> createDecoder;
    private readonly TimeSpan requestTimeout;
    private readonly TimeSpan idleTimeout;
    private readonly Timer idleTimer;
    private readonly LinkedList<(long Second, PlaybackPreviewResult Result)> frames = new();
    private CancellationTokenSource? activeRequest;
    private long activeRequestInstance;
    private IMpvPreviewDecoder? decoder;
    private PlaybackInfo? source;
    private long sourceInstance;
    private long cacheBytes;
    private DateTimeOffset lastAccess;
    private DateTimeOffset retryAfter;
    private PlaybackPreviewError lastError;
    private bool disposed;
    private int foregroundWaiters;

    public MpvPlaybackPreviewService() : this(() => new MpvPreviewDecoder(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)) { }

    internal MpvPlaybackPreviewService(Func<IMpvPreviewDecoder> createDecoder, TimeSpan requestTimeout, TimeSpan idleTimeout,
        TimeSpan? prefetchDelay = null)
    {
        this.createDecoder = createDecoder;
        this.requestTimeout = requestTimeout;
        this.idleTimeout = idleTimeout;
        this.prefetchDelay = prefetchDelay ?? TimeSpan.FromMilliseconds(150);
        idleTimer = new Timer(_ => _ = CleanupIdleAsync(), null, idleTimeout, idleTimeout);
        prefetchWorker = Task.Run(PrefetchLoopAsync);
    }

    public async Task<PlaybackPreviewResult> GetPreviewAsync(long playbackInstanceId, PlaybackInfo info,
        TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!CanPreview(info))
            return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
        using var timeout = new CancellationTokenSource(requestTimeout);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        lock (requestLock)
        {
            if (disposed) return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
            activeRequest?.Cancel();
            activeRequest = request;
            activeRequestInstance = playbackInstanceId;
            if (prefetchState is { } warm && !warm.Matches(playbackInstanceId, info)) RetirePrefetchState();
        }
        var acquired = false;
        Interlocked.Increment(ref foregroundWaiters);
        try
        {
            await gate.WaitAsync(request.Token).ConfigureAwait(false);
            acquired = true;
            DisposeRetiredPrefetchStates();
            var result = await Task.Run(() => DecodeCore(playbackInstanceId, info, position, request.Token), request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            // A cancelled seek must not leave restart events or network reads for the next request.
            if (acquired) await Task.Run(DestroyDecoder).ConfigureAwait(false);
            var error = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && IsCurrentRequest(request)
                ? PlaybackPreviewError.ServerTimeout : PlaybackPreviewError.Cancelled;
            if (acquired && error == PlaybackPreviewError.ServerTimeout)
            {
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
                lastError = error;
            }
            return PlaybackPreviewResult.Failure(error);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Local preview unavailable: {exception.GetType().Name}");
            if (acquired)
            {
                await Task.Run(DestroyDecoder).ConfigureAwait(false);
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
                lastError = PlaybackPreviewError.Unavailable;
            }
            return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
        }
        finally
        {
            if (acquired) gate.Release();
            Interlocked.Decrement(ref foregroundWaiters);
            lock (requestLock) if (ReferenceEquals(activeRequest, request)) activeRequest = null;
            SignalPrefetch();
        }
    }

    private PlaybackPreviewResult DecodeCore(long instance, PlaybackInfo info, TimeSpan position, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool changed;
        lock (cacheLock)
        {
            changed = sourceInstance != instance || !ReferenceEquals(source, info);
            if (changed)
            {
                frames.Clear(); cacheBytes = 0;
                source = info; sourceInstance = instance;
            }
        }
        if (changed) { DestroyDecoder(); retryAfter = default; }
        lastAccess = DateTimeOffset.UtcNow;
        var seconds = PreviewSecond(info, position);
        lock (cacheLock)
        {
            for (var node = frames.First; node is not null; node = node.Next)
            {
                if (node.Value.Second != seconds) continue;
                frames.Remove(node); frames.AddFirst(node);
                return node.Value.Result;
            }
        }
        if (DateTimeOffset.UtcNow < retryAfter) return PlaybackPreviewResult.Failure(lastError);
        decoder ??= createDecoder();
        var result = decoder.Decode(info, TimeSpan.FromSeconds(seconds), token);
        token.ThrowIfCancellationRequested();
        if (result.ImageBytes is { Length: > 0 and <= MaximumCacheBytes } bytes)
        {
            lock (cacheLock)
            {
                frames.AddFirst((seconds, result)); cacheBytes += bytes.Length;
                while (frames.Count > MaximumFrames || cacheBytes > MaximumCacheBytes)
                {
                    cacheBytes -= frames.Last!.Value.Result.ImageBytes!.Length;
                    frames.RemoveLast();
                }
            }
        }
        else
        {
            DestroyDecoder();
            retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            lastError = result.Error;
        }
        return result;
    }

    public async Task ReleaseAsync(long? playbackInstanceId = null)
    {
        lock (requestLock)
        {
            if (playbackInstanceId is null || activeRequestInstance == playbackInstanceId) activeRequest?.Cancel();
            if (prefetchState is { } warm && (playbackInstanceId is null || warm.Instance == playbackInstanceId))
            {
                RetirePrefetchState();
            }
        }
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeRetiredPrefetchStates();
            if (playbackInstanceId is null || sourceInstance == playbackInstanceId)
                await Task.Run(ClearSource).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task CleanupIdleAsync()
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (DateTimeOffset.UtcNow - lastAccess >= idleTimeout)
            {
                // Keep sparse images and failure cooldown; only the decoder is an idle resource.
                DestroyDecoder();
            }
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Preview cleanup failed: {exception.GetType().Name}"); }
        finally { gate.Release(); }
    }

    private bool IsCurrentRequest(CancellationTokenSource request)
    {
        lock (requestLock) return ReferenceEquals(activeRequest, request);
    }

    private void DestroyDecoder() { var old = decoder; decoder = null; old?.Dispose(); }
    private void ClearSource()
    {
        lock (cacheLock) { frames.Clear(); cacheBytes = 0; source = null; }
        DestroyDecoder(); retryAfter = default;
    }

    public async ValueTask DisposeAsync()
    {
        lock (requestLock) { disposed = true; activeRequest?.Cancel(); }
        prefetchStop.Cancel();
        SignalPrefetch();
        await idleTimer.DisposeAsync().ConfigureAwait(false);
        await ReleaseAsync().ConfigureAwait(false);
        await prefetchWorker.ConfigureAwait(false);
    }
}
