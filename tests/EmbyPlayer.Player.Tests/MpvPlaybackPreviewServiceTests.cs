using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Player.Tests;

[TestClass]
public sealed class MpvPlaybackPreviewServiceTests
{
    [TestMethod]
    public async Task QuantizedFramesReuseCacheAndClampToDuration()
    {
        var fake = new FakeDecoder();
        await using var service = Create(() => fake);
        var info = Info();
        await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(4.2), default);
        await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(4.8), default);
        await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(-20), default);
        await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(200), default);
        CollectionAssert.AreEqual(new[] { 4d, 0d, 59d }, fake.Positions.ToArray());
    }

    [TestMethod]
    public async Task NewSourceAndPlaybackInstanceNeverShareFrames()
    {
        var decoders = new List<FakeDecoder>();
        await using var service = Create(() => { var decoder = new FakeDecoder(); decoders.Add(decoder); return decoder; });
        var info = Info();
        await service.GetPreviewAsync(1, info, TimeSpan.Zero, default);
        await service.GetPreviewAsync(2, info, TimeSpan.Zero, default);
        await service.GetPreviewAsync(2, info with { PlaybackPath = "other.mkv" }, TimeSpan.Zero, default);
        Assert.AreEqual(3, decoders.Count);
        Assert.IsTrue(decoders[0].Disposed && decoders[1].Disposed);
    }

    [TestMethod]
    public async Task ReleaseOfOldInstanceCannotDisposeNewDecoder()
    {
        var fake = new FakeDecoder();
        await using var service = Create(() => fake);
        var info = Info();
        await service.GetPreviewAsync(2, info, TimeSpan.Zero, default);
        await service.ReleaseAsync(1);
        Assert.IsFalse(fake.Disposed);
        await service.GetPreviewAsync(2, info, TimeSpan.Zero, default);
        Assert.AreEqual(1, fake.Positions.Count);
        await service.ReleaseAsync(2);
        Assert.IsTrue(fake.Disposed);
    }

    [TestMethod]
    public async Task NewRequestCancelsOldWorkBeforeCreatingNextDecoder()
    {
        using var started = new ManualResetEventSlim();
        var decoders = new List<FakeDecoder>();
        await using var service = Create(() =>
        {
            var fake = new FakeDecoder();
            if (decoders.Count == 0) fake.DecodeHandler = (_, token) =>
            {
                started.Set(); token.WaitHandle.WaitOne(2000); token.ThrowIfCancellationRequested();
                Assert.Fail("Old decode was not cancelled."); return Image(TimeSpan.Zero);
            };
            decoders.Add(fake); return fake;
        });
        var info = Info();
        var previous = service.GetPreviewAsync(1, info, TimeSpan.Zero, default);
        Assert.IsTrue(started.Wait(2000));
        var current = service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(4), default);
        Assert.AreEqual(PlaybackPreviewError.Cancelled, (await previous).Error);
        Assert.AreEqual(PlaybackPreviewError.None, (await current).Error);
        Assert.AreEqual(2, decoders.Count);
        Assert.IsTrue(decoders[0].Disposed);
        Assert.IsFalse(decoders[0].DisposedWhileDecoding);
    }

    [TestMethod]
    public async Task ReleaseCancelsInFlightWorkWithoutCallingItTimeout()
    {
        using var started = new ManualResetEventSlim();
        var fake = new FakeDecoder { DecodeHandler = (_, token) =>
        {
            started.Set(); token.WaitHandle.WaitOne(2000); token.ThrowIfCancellationRequested(); return Image(TimeSpan.Zero);
        } };
        await using var service = Create(() => fake);
        var pending = service.GetPreviewAsync(7, Info(), TimeSpan.Zero, default);
        Assert.IsTrue(started.Wait(2000));
        await service.ReleaseAsync(7);
        Assert.AreEqual(PlaybackPreviewError.Cancelled, (await pending).Error);
        Assert.IsTrue(fake.Disposed);
        Assert.IsFalse(fake.DisposedWhileDecoding);
    }

    [TestMethod]
    public async Task TimeoutReleasesDecoderAndBacksOffEvenAfterIdleCleanup()
    {
        var creates = 0;
        var fake = new FakeDecoder { DecodeHandler = (_, token) =>
        {
            token.WaitHandle.WaitOne(2000); token.ThrowIfCancellationRequested(); return Image(TimeSpan.Zero);
        } };
        await using var service = new MpvPlaybackPreviewService(() => { creates++; return fake; }, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20));
        var info = Info();
        Assert.AreEqual(PlaybackPreviewError.ServerTimeout, (await service.GetPreviewAsync(1, info, TimeSpan.Zero, default)).Error);
        await Task.Delay(100);
        Assert.AreEqual(PlaybackPreviewError.ServerTimeout, (await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(2), default)).Error);
        Assert.AreEqual(1, creates);
        Assert.IsTrue(fake.Disposed);
    }

    [TestMethod]
    public async Task FailedDecodeBacksOffButNewSourceCanRetry()
    {
        var fake = new FakeDecoder { DecodeHandler = (_, _) => PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable) };
        await using var service = Create(() => fake);
        var info = Info();
        await service.GetPreviewAsync(1, info, TimeSpan.Zero, default);
        await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(2), default);
        Assert.AreEqual(1, fake.Positions.Count);
        await service.GetPreviewAsync(2, info, TimeSpan.Zero, default);
        Assert.AreEqual(2, fake.Positions.Count);
    }

    [TestMethod]
    public async Task FrameCountAndMemoryLimitsEvictOldestImages()
    {
        foreach (var large in new[] { false, true })
        {
            var fake = new FakeDecoder { DecodeHandler = (position, _) => new(new byte[large ? 500_000 : 64], position, PlaybackPreviewError.None) };
            await using var service = Create(() => fake);
            var info = Info() with { RunTimeTicks = TimeSpan.FromMinutes(3).Ticks };
            var count = large ? 55 : 100;
            for (var i = 0; i < count; i++) await service.GetPreviewAsync(1, info, TimeSpan.FromSeconds(i), default);
            await service.GetPreviewAsync(1, info, TimeSpan.Zero, default);
            Assert.AreEqual(count + 1, fake.Positions.Count);
        }
    }

    [TestMethod]
    public async Task TranscodingUnknownDurationAndOffsetsDoNotStartDecoder()
    {
        var created = false;
        await using var service = Create(() => { created = true; return new FakeDecoder(); });
        foreach (var info in new[] { Info() with { RequiresTranscoding = true }, Info() with { RunTimeTicks = null },
            Info() with { StreamPositionOffsetTicks = 100 }, Info() with { PlaybackPath = null } })
            Assert.AreEqual(PlaybackPreviewError.Unavailable, (await service.GetPreviewAsync(1, info, TimeSpan.Zero, default)).Error);
        Assert.IsFalse(created);
    }

    [TestMethod]
    public async Task DisposeRejectsFutureWorkAndNativeFailureStaysNonfatal()
    {
        var fake = new FakeDecoder { DecodeHandler = (_, _) => throw new DllNotFoundException() };
        var service = Create(() => fake);
        Assert.AreEqual(PlaybackPreviewError.Unavailable, (await service.GetPreviewAsync(1, Info(), TimeSpan.Zero, default)).Error);
        Assert.IsTrue(fake.Disposed);
        await service.DisposeAsync();
        Assert.AreEqual(PlaybackPreviewError.Unavailable, (await service.GetPreviewAsync(2, Info(), TimeSpan.Zero, default)).Error);
    }

    private static MpvPlaybackPreviewService Create(Func<IMpvPreviewDecoder> create) => new(create, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1));
    private static PlaybackPreviewResult Image(TimeSpan position) => new(new byte[64], position, PlaybackPreviewError.None);
    private static PlaybackInfo Info() => new("item", "Test", null,
        new("source", "mkv", true, true, false, new Dictionary<string, string>()), "test.mkv", false, false,
        TimeSpan.FromMinutes(1).Ticks, 0, Array.Empty<PlaybackTrack>(), Array.Empty<PlaybackSubtitle>());
    private sealed class FakeDecoder : IMpvPreviewDecoder
    {
        private bool decoding;
        public List<double> Positions { get; } = new();
        public bool Disposed { get; private set; }
        public bool DisposedWhileDecoding { get; private set; }
        public Func<TimeSpan, CancellationToken, PlaybackPreviewResult> DecodeHandler { get; set; } = (position, _) => Image(position);
        public PlaybackPreviewResult Decode(PlaybackInfo info, TimeSpan position, CancellationToken cancellationToken)
        {
            decoding = true;
            try { Positions.Add(position.TotalSeconds); return DecodeHandler(position, cancellationToken); }
            finally { decoding = false; }
        }
        public void Dispose() { Disposed = true; DisposedWhileDecoding |= decoding; }
    }
}
