using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Player;

public sealed partial class MpvPlaybackPreviewService
{
    private const int SparseFrameCount = 80;
    private readonly TimeSpan prefetchDelay;
    private readonly SemaphoreSlim prefetchSignal = new(0, 1);
    private readonly CancellationTokenSource prefetchStop = new();
    private readonly Task prefetchWorker;
    private PrefetchState? prefetchState;
    private readonly List<PrefetchState> retiredPrefetchStates = new();

    public PlaybackPreviewResult? TryGetCachedPreview(long playbackInstanceId, PlaybackInfo info, TimeSpan position)
    {
        if (!CanPreview(info)) return null;
        lock (requestLock) if (disposed) return null;
        lock (cacheLock)
        {
            if (sourceInstance != playbackInstanceId || !ReferenceEquals(source, info) || frames.Count == 0) return null;
            var second = PreviewSecond(info, position);
            var nearest = frames.MinBy(frame => Math.Abs(frame.Second - second));
            if (nearest.Result is null || Math.Abs(nearest.Second - second) > Math.Max(5, SparseInterval(info))) return null;
            return nearest.Result;
        }
    }

    public void Prefetch(long playbackInstanceId, PlaybackInfo info, TimeSpan position, CancellationToken playbackLifetimeToken)
    {
        if (!CanPreview(info) || playbackLifetimeToken.IsCancellationRequested) return;
        lock (requestLock)
        {
            if (disposed) return;
            if (prefetchState is not { } warm || !warm.Matches(playbackInstanceId, info) || warm.Cancellation.IsCancellationRequested)
            {
                RetirePrefetchState();
                prefetchState = new PrefetchState(playbackInstanceId, info,
                    CancellationTokenSource.CreateLinkedTokenSource(playbackLifetimeToken, prefetchStop.Token));
            }
            prefetchState.PointerSecond = PreviewSecond(info, position);
        }
        SignalPrefetch();
    }

    private async Task PrefetchLoopAsync()
    {
        try
        {
            while (!prefetchStop.IsCancellationRequested)
            {
                await prefetchSignal.WaitAsync(prefetchStop.Token).ConfigureAwait(false);
                while (!prefetchStop.IsCancellationRequested)
                {
                    PrefetchState? warm;
                    lock (requestLock) warm = prefetchState;
                    if (warm is null || warm.Cancellation.IsCancellationRequested) break;
                    // A foreground request gets the next native slot. Never cancel an active decode for a pointer move.
                    if (Volatile.Read(ref foregroundWaiters) > 0 || !await gate.WaitAsync(0).ConfigureAwait(false))
                    {
                        await Task.Delay(50, prefetchStop.Token).ConfigureAwait(false);
                        continue;
                    }
                    var noWork = false;
                    try
                    {
                        DisposeRetiredPrefetchStates();
                        lock (requestLock)
                            if (!ReferenceEquals(warm, prefetchState) || warm.Cancellation.IsCancellationRequested) continue;
                        if (Volatile.Read(ref foregroundWaiters) > 0) continue;
                        if (sourceInstance == warm.Instance && ReferenceEquals(source, warm.Info) && retryAfter > DateTimeOffset.UtcNow)
                            noWork = true;
                        else
                        {
                            var target = NextPrefetchSecond(warm);
                            if (target is null) noWork = true;
                            else
                            {
                                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(warm.Cancellation.Token);
                                timeout.CancelAfter(requestTimeout);
                                try
                                {
                                    var result = DecodeCore(warm.Instance, warm.Info, TimeSpan.FromSeconds(target.Value), timeout.Token);
                                    if (result.ImageBytes is null) noWork = true;
                                }
                                catch (OperationCanceledException)
                                {
                                    DestroyDecoder();
                                    if (!warm.Cancellation.IsCancellationRequested)
                                    { retryAfter = DateTimeOffset.UtcNow.AddSeconds(30); lastError = PlaybackPreviewError.ServerTimeout; }
                                    noWork = true;
                                }
                                catch (Exception exception)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Preview warmup unavailable: {exception.GetType().Name}");
                                    DestroyDecoder(); retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
                                    lastError = PlaybackPreviewError.Unavailable; noWork = true;
                                }
                            }
                        }
                    }
                    finally { gate.Release(); }
                    if (noWork) break;
                    await Task.Delay(prefetchDelay, prefetchStop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (prefetchStop.IsCancellationRequested) { }
    }

    // The queue is finite, so frames evicted by the memory cap do not cause an endless re-download loop.
    private long? NextPrefetchSecond(PrefetchState warm)
    {
        long pointer;
        lock (requestLock) pointer = warm.PointerSecond;
        lock (cacheLock)
        {
            var sameSource = sourceInstance == warm.Instance && ReferenceEquals(source, warm.Info);
            bool Cached(long second) => sameSource && frames.Any(frame => frame.Second == second);
            foreach (var offset in new[] { 0, -2, 2, -5, 5 })
            {
                var second = Math.Clamp(pointer + offset, 0, LastSecond(warm.Info));
                if (!Cached(second)) return second;
            }
            while (warm.SparseIndex < SparseFrameCount)
            {
                var second = (long)Math.Round(LastSecond(warm.Info) * (warm.SparseIndex++ / (double)(SparseFrameCount - 1)));
                if (!Cached(second)) return second;
            }
            return null;
        }
    }

    private void SignalPrefetch()
    {
        try { prefetchSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    // Retire under requestLock; dispose only while holding the native gate so an old worker cannot read its Token.
    private void RetirePrefetchState()
    {
        if (prefetchState is not { } old) return;
        old.Cancellation.Cancel();
        retiredPrefetchStates.Add(old);
        prefetchState = null;
    }

    private void DisposeRetiredPrefetchStates()
    {
        lock (requestLock)
        {
            foreach (var retired in retiredPrefetchStates) retired.Cancellation.Dispose();
            retiredPrefetchStates.Clear();
        }
    }

    private static bool CanPreview(PlaybackInfo info) => !info.RequiresTranscoding && info.RunTimeTicks is > 0
        && !string.IsNullOrWhiteSpace(info.PlaybackPath) && info.StreamPositionOffsetTicks == 0
        && (info.MediaSource.SupportsDirectPlay || info.MediaSource.SupportsDirectStream);
    private static long LastSecond(PlaybackInfo info) => Math.Max(0, (info.RunTimeTicks!.Value - TimeSpan.TicksPerSecond) / TimeSpan.TicksPerSecond);
    private static long PreviewSecond(PlaybackInfo info, TimeSpan position) => Math.Clamp((long)position.TotalSeconds, 0, LastSecond(info));
    private static double SparseInterval(PlaybackInfo info) => LastSecond(info) / (double)(SparseFrameCount - 1);

    private sealed class PrefetchState(long instance, PlaybackInfo info, CancellationTokenSource cancellation)
    {
        public long Instance { get; } = instance;
        public PlaybackInfo Info { get; } = info;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public long PointerSecond { get; set; }
        public int SparseIndex { get; set; }
        public bool Matches(long targetInstance, PlaybackInfo targetInfo) => Instance == targetInstance && ReferenceEquals(Info, targetInfo);
    }
}
