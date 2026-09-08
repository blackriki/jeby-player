using System.Collections.Concurrent;
using System.Diagnostics;
using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Player.Tests;

[TestClass]
public sealed class MpvSmoothPreviewTests
{
    [TestMethod]
    public async Task ColdCacheLookupIsImmediateWhileNativeDecodeIsBusy()
    {
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var fake = new Decoder((position, token) => { started.Set(); finish.Wait(token); return Image(position); });
        await using var service = Create(() => fake);
        var info = Info();
        Assert.IsNull(service.TryGetCachedPreview(1, info, TimeSpan.Zero));
        var pending = service.GetPreviewAsync(1, info, TimeSpan.Zero, default);
        Assert.IsTrue(started.Wait(2000));
        var watch = Stopwatch.StartNew();
        Assert.IsNull(service.TryGetCachedPreview(1, info, TimeSpan.Zero));
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(100), "A cache lookup must not wait for the native gate.");
        finish.Set(); await pending;
        Assert.IsNotNull(service.TryGetCachedPreview(1, info, TimeSpan.Zero));
    }

    [TestMethod]
    public async Task NearestCachedFrameReportsItsActualTimeAndCannotCrossSource()
    {
        await using var service = Create(() => new Decoder((position, _) => Image(position)));
        var info = Info();
        await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(30), default);
        Assert.AreEqual(TimeSpan.FromSeconds(30), service.TryGetCachedPreview(1, info, TimeSpan.FromSeconds(34))?.ImagePosition);
        Assert.IsNull(service.TryGetCachedPreview(1, info, TimeSpan.FromSeconds(100)));
        Assert.IsNull(service.TryGetCachedPreview(2, info, TimeSpan.FromSeconds(30)));
        Assert.IsNull(service.TryGetCachedPreview(1, info with { PlaybackPath = "other.mkv" }, TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    public async Task CachedImagesSurviveIdleDecoderReleaseButNotExplicitRelease()
    {
        var fake = new Decoder((position, _) => Image(position));
        await using var service = new MpvPlaybackPreviewService(() => fake, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20));
        var info = Info();
        await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(20), default);
        await Until(() => fake.Disposed);
        Assert.IsNotNull(service.TryGetCachedPreview(1, info, TimeSpan.FromSeconds(20)));
        await service.ReleaseAsync(1);
        Assert.IsNull(service.TryGetCachedPreview(1, info, TimeSpan.FromSeconds(20)));
    }

    [TestMethod]
    public async Task SparseWarmupCoversWholeVideoAndTerminatesAfterFiniteWork()
    {
        var positions = new ConcurrentQueue<double>();
        var fake = new Decoder((position, _) => { positions.Enqueue(position.TotalSeconds); return Image(position); });
        await using var service = Create(() => fake);
        var info = Info();
        service.Prefetch(1, info, TimeSpan.FromSeconds(120), default);
        await Until(() => positions.Contains(299));
        var count = positions.Count;
        await Task.Delay(100);
        Assert.AreEqual(count, positions.Count, "Completed sparse work must not restart itself.");
        Assert.IsTrue(count <= 85);
        foreach (var second in new[] { 0, 75, 150, 225, 299 })
            Assert.IsNotNull(service.TryGetCachedPreview(1, info, TimeSpan.FromSeconds(second)), $"No sparse frame near {second}.");
        Assert.AreEqual(120d, positions.First(), "Pointer frame precedes sparse coverage.");
    }

    [TestMethod]
    public async Task ForegroundGetsNextSlotAndPointerUpdatesDoNotCancelCurrentDecode()
    {
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var calls = new ConcurrentQueue<double>();
        var creates = 0;
        var fake = new Decoder((position, token) =>
        {
            calls.Enqueue(position.TotalSeconds);
            if (calls.Count == 1) { started.Set(); finish.Wait(token); }
            return Image(position);
        });
        await using var service = Create(() => { creates++; return fake; });
        var info = Info();
        service.Prefetch(1, info, TimeSpan.FromSeconds(100), default);
        Assert.IsTrue(started.Wait(2000));
        var foreground = service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(77), default);
        for (var second = 150; second <= 200; second++) service.Prefetch(1, info, TimeSpan.FromSeconds(second), default);
        Assert.IsFalse(fake.Disposed);
        finish.Set();
        Assert.AreEqual(PlaybackPreviewError.None, (await foreground).Error);
        await Until(() => calls.Contains(200));
        Assert.AreEqual(77d, calls.ElementAt(1));
        Assert.AreEqual(1, creates, "Pointer movement must not destroy and recreate the preview decoder.");
        Assert.IsFalse(fake.DisposedWhileDecoding);
    }

    [TestMethod]
    public async Task LifetimeCancellationStopsWarmupAndReleaseClearsItsCache()
    {
        using var lifetime = new CancellationTokenSource();
        var count = 0;
        var fake = new Decoder((position, _) => { Interlocked.Increment(ref count); return Image(position); });
        await using var service = Create(() => fake);
        var info = Info();
        service.Prefetch(1, info, TimeSpan.FromSeconds(30), lifetime.Token);
        await Until(() => Volatile.Read(ref count) >= 2);
        lifetime.Cancel();
        await Task.Delay(30);
        var stoppedCount = Volatile.Read(ref count);
        await Task.Delay(60);
        Assert.AreEqual(stoppedCount, Volatile.Read(ref count));
        await service.ReleaseAsync(1);
        Assert.IsNull(service.TryGetCachedPreview(1, info, TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    public async Task ReplacingSourceCancelsOldWarmupWithoutServingItsFrames()
    {
        using var started = new ManualResetEventSlim();
        var creates = 0;
        var old = new Decoder((_, token) => { started.Set(); token.WaitHandle.WaitOne(2000); token.ThrowIfCancellationRequested(); return Image(TimeSpan.Zero); });
        var next = new Decoder((position, _) => Image(position));
        await using var service = Create(() => Interlocked.Increment(ref creates) == 1 ? old : next);
        var info = Info(); var other = info with { PlaybackPath = "other.mkv" };
        service.Prefetch(1, info, TimeSpan.Zero, default);
        Assert.IsTrue(started.Wait(2000));
        service.Prefetch(2, other, TimeSpan.FromSeconds(20), default);
        await Until(() => service.TryGetCachedPreview(2, other, TimeSpan.FromSeconds(20)) is not null);
        Assert.IsNull(service.TryGetCachedPreview(1, info, TimeSpan.Zero));
        Assert.IsTrue(old.Disposed);
        Assert.IsFalse(old.DisposedWhileDecoding);
        await service.ReleaseAsync(1);
        Assert.IsNotNull(service.TryGetCachedPreview(2, other, TimeSpan.FromSeconds(20)));
    }

    [TestMethod]
    public async Task ByteEvictionDoesNotRestartSparseQueueForever()
    {
        var calls = new ConcurrentQueue<double>();
        var fake = new Decoder((position, _) => { calls.Enqueue(position.TotalSeconds); return new(new byte[500_000], position, PlaybackPreviewError.None); });
        await using var service = Create(() => fake);
        var info = Info();
        service.Prefetch(1, info, TimeSpan.FromSeconds(150), default);
        await Until(() => calls.Contains(299));
        await Task.Delay(60);
        var count = calls.Count;
        await Task.Delay(100);
        Assert.AreEqual(count, calls.Count);
        Assert.IsTrue(count < 100, "Evicted sparse entries must not regenerate forever.");
    }

    private static MpvPlaybackPreviewService Create(Func<IMpvPreviewDecoder> factory) => new(factory, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1));
    private static PlaybackPreviewResult Image(TimeSpan position) => new(new byte[64], position, PlaybackPreviewError.None);
    private static PlaybackInfo Info() => new("item", "Test", null, new("source", "mkv", true, true, false, new Dictionary<string, string>()),
        "test.mkv", false, false, TimeSpan.FromMinutes(5).Ticks, 0, Array.Empty<PlaybackTrack>(), Array.Empty<PlaybackSubtitle>());
    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(5);
        Assert.IsTrue(condition(), "Background preview operation exceeded deadline.");
    }
    private sealed class Decoder(Func<TimeSpan, CancellationToken, PlaybackPreviewResult> decode) : IMpvPreviewDecoder
    {
        private bool decoding;
        public volatile bool Disposed;
        public bool DisposedWhileDecoding;
        public PlaybackPreviewResult Decode(PlaybackInfo info, TimeSpan position, CancellationToken cancellationToken)
        {
            decoding = true;
            try { return decode(position, cancellationToken); }
            finally { decoding = false; }
        }
        public void Dispose() { DisposedWhileDecoding |= decoding; Disposed = true; }
    }
}
