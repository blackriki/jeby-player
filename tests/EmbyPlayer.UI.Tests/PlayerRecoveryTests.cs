using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task PlaybackRecovery_RetryPreservesPausedPositionVolumeSpeedAndRejectsLateEvents()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var original = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new(original, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(90), 13.33, false));
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Paused));
        await context.ViewModel.SetVolumeAsync(37);
        await context.ViewModel.SetPlaybackSpeedAsync(1.5);
        await context.ViewModel.ToggleMuteAsync();
        await context.ViewModel.AdjustSubtitleAsync("larger");
        await context.ViewModel.AdjustSubtitleAsync("later");
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        Assert.IsTrue(context.ViewModel.CanRetryPlayback);
        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
        context.PlayerService.RaiseProgressChanged(new(original, TimeSpan.Zero, TimeSpan.FromMinutes(90), 0, false));
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Playing));
        Assert.IsTrue(context.ViewModel.HasError);

        ConfigureQualitySuccess(context);
        await context.ViewModel.RetryPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(TimeSpan.FromMinutes(12).Ticks, context.PlayerService.LastLoadRequest.PlaybackInfo.StartPositionTicks);
        Assert.IsTrue(context.PlayerService.LastLoadRequest.StartPaused);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual(37, context.ViewModel.Volume);
        Assert.AreEqual(1.5, context.ViewModel.SelectedPlaybackSpeed);
        Assert.IsTrue(context.ViewModel.IsMuted);
        Assert.AreEqual(1.1, context.ViewModel.SubtitleScale);
        Assert.AreEqual(0.1, context.ViewModel.SubtitleDelaySeconds);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.IsFalse(context.ViewModel.IsRetryingPlayback);
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        Assert.IsFalse(context.ViewModel.HasError);
    }

    [TestMethod]
    public async Task PlaybackRecovery_ServerStillUnavailableKeepsSnapshotForExplicitRetry()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var original = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new(original, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(90), 5.55, false));
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) =>
            Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.ServerTimeout));
        await context.ViewModel.RetryPlaybackAsync();
        Assert.IsTrue(context.ViewModel.HasError);
        Assert.IsTrue(context.ViewModel.CanRetryPlayback);
        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);
        ConfigureQualitySuccess(context);
        await context.ViewModel.RetryPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.FromMinutes(5).Ticks, context.PlayerService.LastLoadRequest.PlaybackInfo.StartPositionTicks);
        Assert.IsFalse(context.ViewModel.HasError);
    }

    [TestMethod]
    public async Task PlaybackRecovery_NativeRetryFailureRetainsPauseIntentForNextRetry()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var original = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new(original, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(90), 7.77, false));
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Paused));
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        ConfigureQualitySuccess(context);
        context.PlayerService.LoadAsyncHandler = (_, _) => Task.FromResult(PlayerOperationResult.Failure(PlayerError.LoadFailed));
        await context.ViewModel.RetryPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(context.ViewModel.CanRetryPlayback);
        ConfigureQualitySuccess(context);
        await context.ViewModel.RetryPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(context.PlayerService.LastLoadRequest.StartPaused);
        Assert.AreEqual(TimeSpan.FromMinutes(7).Ticks, context.PlayerService.LastLoadRequest.PlaybackInfo.StartPositionTicks);
        Assert.IsFalse(context.ViewModel.HasError);
    }

    [TestMethod]
    public async Task PlaybackRecovery_NavigationCancelsPendingRetryAndDuplicateRetryIsIgnored()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var original = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseStatusChanged(new(original, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        var pending = new TaskCompletionSource<PlaybackLoadResult>();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => pending.Task;
        var retry = context.ViewModel.RetryPlaybackAsync();
        Assert.IsTrue(context.ViewModel.IsRetryingPlayback);
        await context.ViewModel.RetryPlaybackAsync();
        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        pending.SetResult(PlaybackLoadResult.Success(CreatePlaybackInfo()));
        await retry;
        Assert.AreEqual(original, context.PlayerService.LastLoadRequest.PlaybackInstanceId);
        Assert.IsFalse(context.ViewModel.IsRetryingPlayback);
    }
}
