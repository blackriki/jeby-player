using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Player.Tests;

public sealed partial class MpvPlayerServiceLifecycleTests
{
    [TestMethod]
    public async Task PlaybackSpeed_SlowFastAndRestoreUseInvariantNativeProperty()
    {
        var native = new FakeMpvNativeApi();
        await using var service = CreateService(native);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (var speed in new[] { 0.25, 0.5, 0.75, 1.25, 1.5, 1.75, 2, 4, 1d })
            {
                Assert.IsTrue((await service.SetPlaybackSpeedAsync(42, speed, CancellationToken.None)).IsSuccess);
                CollectionAssert.Contains(native.PropertySets.ToArray(), "speed=" + speed.ToString("G", CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    public async Task PlaybackSpeed_InvalidValuesAndStaleInstancesDoNotTouchNativeState()
    {
        var native = new FakeMpvNativeApi();
        await using var service = CreateService(native);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        var count = native.PropertySets.Count;
        foreach (var speed in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1, 0.24, 4.01 })
        {
            Assert.AreEqual(PlayerError.PlaybackSpeedFailed, (await service.SetPlaybackSpeedAsync(42, speed, CancellationToken.None)).Error);
        }
        Assert.IsFalse((await service.SetPlaybackSpeedAsync(0, 2, CancellationToken.None)).IsSuccess);
        Assert.IsFalse((await service.SetPlaybackSpeedAsync(41, 2, CancellationToken.None)).IsSuccess);
        Assert.AreEqual(count, native.PropertySets.Count);
        await service.StopAsync(CancellationToken.None);
        count = native.PropertySets.Count;
        Assert.IsFalse((await service.SetPlaybackSpeedAsync(42, 2, CancellationToken.None)).IsSuccess);
        Assert.AreEqual(count, native.PropertySets.Count);
    }

    [TestMethod]
    public async Task PlaybackSpeed_NativeFailureIsRecoverableAndCancellationDoesNotWrite()
    {
        var native = new FakeMpvNativeApi { FailedProperty = "speed" };
        await using var service = CreateService(native);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        Assert.AreEqual(PlayerError.PlaybackSpeedFailed, (await service.SetPlaybackSpeedAsync(42, 2, CancellationToken.None)).Error);
        native.FailedProperty = null;
        Assert.IsTrue((await service.SetPlaybackSpeedAsync(42, 1, CancellationToken.None)).IsSuccess);
        var count = native.PropertySets.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => service.SetPlaybackSpeedAsync(42, 2, cancellation.Token));
        Assert.AreEqual(count, native.PropertySets.Count);
    }

    [TestMethod]
    public async Task PlaybackSpeed_NewLoadStartsNormalWithPitchCorrectionAndRejectsOldRestore()
    {
        var native = new FakeMpvNativeApi();
        await using var service = CreateService(native);
        await service.LoadAsync(CreateRequest(), CancellationToken.None);
        Assert.IsTrue((await service.SetPlaybackSpeedAsync(42, 2, CancellationToken.None)).IsSuccess);
        await service.LoadAsync(CreateRequest(playbackInstanceId: 43), CancellationToken.None);
        Assert.AreEqual(2, native.OptionSets.Count(value => value == "speed=1"));
        Assert.AreEqual(2, native.OptionSets.Count(value => value == "audio-pitch-correction=yes"));
        var count = native.PropertySets.Count;
        Assert.IsFalse((await service.SetPlaybackSpeedAsync(42, 1, CancellationToken.None)).IsSuccess);
        Assert.AreEqual(count, native.PropertySets.Count);
        Assert.IsTrue((await service.SetPlaybackSpeedAsync(43, 0.5, CancellationToken.None)).IsSuccess);
    }
}
