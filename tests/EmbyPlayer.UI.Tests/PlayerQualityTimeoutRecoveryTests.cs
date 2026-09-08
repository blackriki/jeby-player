using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task QualitySwitch_ServerTimeoutPreservesPausedStreamAndSubsequentRetrySucceeds()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var original = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new(original, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(90), 13.33, false));
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Paused));
        await context.ViewModel.SetVolumeAsync(37);
        var stops = context.PlayerService.StopCallCount;
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerTimeout));

        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);

        Assert.AreEqual(original, context.PlayerService.LastLoadRequest.PlaybackInstanceId);
        Assert.AreEqual(stops, context.PlayerService.StopCallCount);
        Assert.AreEqual(PlaybackQuality.Original, context.ViewModel.PlaybackInfo!.Quality);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.IsTrue(context.ViewModel.CanSwitchQuality);
        Assert.IsFalse(context.ViewModel.IsSwitchingQuality);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "保持原播放");

        ConfigureQualitySuccess(context);
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(PlaybackQuality.Hd720, context.ViewModel.PlaybackInfo!.Quality);
        Assert.AreEqual(TimeSpan.FromMinutes(12).Ticks, context.PlayerService.LastLoadRequest.PlaybackInfo.StartPositionTicks);
        Assert.IsTrue(context.PlayerService.LastLoadRequest.StartPaused);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual(37, context.ViewModel.Volume);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.IsFalse(context.ViewModel.IsSwitchingQuality);
    }

    [TestMethod]
    public async Task QualitySwitch_TranscodeStreamFailureRestoresOriginalAndAllowsFreshRetry()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        ConfigureQualitySuccess(context);
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            if (request.PlaybackInfo.Quality == PlaybackQuality.Original)
                context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Playing));
            return Task.FromResult(PlayerOperationResult.Success());
        };

        var changing = context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);
        var failedInstance = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseStatusChanged(new(failedInstance, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        await changing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(PlaybackQuality.Original, context.ViewModel.PlaybackInfo!.Quality);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsTrue(context.ViewModel.CanSwitchQuality);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "已恢复原画质");

        ConfigureQualitySuccess(context);
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720).WaitAsync(TimeSpan.FromSeconds(3));
        var recoveredInstance = context.PlayerService.LastLoadRequest.PlaybackInstanceId;
        context.PlayerService.RaiseStatusChanged(new(failedInstance, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        Assert.AreNotEqual(failedInstance, recoveredInstance);
        Assert.AreEqual(recoveredInstance, context.PlayerService.LastLoadRequest.PlaybackInstanceId);
        Assert.AreEqual(PlaybackQuality.Hd720, context.ViewModel.PlaybackInfo!.Quality);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual(3, context.PlaybackPreparationService.PrepareCallCount);
    }
}
