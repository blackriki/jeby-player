using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task PlaybackSpeed_InvalidValuesDoNotChangeSelectionOrCallNativeService()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        foreach (var speed in new[] { double.NaN, double.PositiveInfinity, 0, -1, 0.24, 4.01 })
        {
            await context.ViewModel.SetPlaybackSpeedAsync(speed);
        }
        Assert.AreEqual(1d, context.ViewModel.PlaybackSpeed);
        Assert.AreEqual(1d, context.ViewModel.SelectedPlaybackSpeed);
        Assert.AreEqual(0, context.PlayerService.PlaybackSpeedCalls.Count);
    }

    [TestMethod]
    public async Task PlaybackSpeed_SelectionAndTemporaryHoldRestoreWithoutPause()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await context.ViewModel.SetPlaybackSpeedAsync(0.5);
        Assert.AreEqual(0.5, context.ViewModel.PlaybackSpeed);
        await context.ViewModel.SetPlaybackSpeedAsync(1.5);
        await context.ViewModel.BeginTemporaryDoubleSpeedAsync();
        Assert.AreEqual(2d, context.ViewModel.PlaybackSpeed);
        Assert.AreEqual(1.5, context.ViewModel.SelectedPlaybackSpeed);
        Assert.IsTrue(context.ViewModel.IsTemporaryDoubleSpeed);
        await context.ViewModel.EndTemporaryDoubleSpeedAsync();
        Assert.AreEqual(1.5, context.ViewModel.PlaybackSpeed);
        Assert.IsFalse(context.ViewModel.IsTemporaryDoubleSpeed);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.AreEqual(0, context.PlayerService.PauseCallCount);
        CollectionAssert.AreEqual(new[] { 0.5, 1.5, 2, 1.5 }, context.PlayerService.PlaybackSpeedCalls.Select(call => call.Speed).ToArray());
    }

    [TestMethod]
    public async Task PlaybackSpeed_PausedHoldDoesNothingButMenuSelectionWorks()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Paused));
        await context.ViewModel.BeginTemporaryDoubleSpeedAsync();
        await context.ViewModel.EndTemporaryDoubleSpeedAsync();
        Assert.AreEqual(0, context.PlayerService.PlaybackSpeedCalls.Count);
        await context.ViewModel.SetPlaybackSpeedAsync(0.5);
        Assert.AreEqual(0.5, context.ViewModel.PlaybackSpeed);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual(0, context.PlayerService.PlayCallCount);
    }

    [TestMethod]
    public async Task PlaybackSpeed_ReleaseWhileBoostPendingEventuallyRestoresSelectedSpeed()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await context.ViewModel.SetPlaybackSpeedAsync(1.5);
        var pending = new TaskCompletionSource<PlayerOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlayerService.PlaybackSpeedHandler = (_, speed, _) => speed == 2 ? pending.Task : Task.FromResult(PlayerOperationResult.Success());
        var press = context.ViewModel.BeginTemporaryDoubleSpeedAsync();
        var release = context.ViewModel.EndTemporaryDoubleSpeedAsync();
        Assert.IsFalse(release.IsCompleted);
        pending.SetResult(PlayerOperationResult.Success());
        await Task.WhenAll(press, release).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1.5, context.ViewModel.PlaybackSpeed);
        Assert.IsFalse(context.ViewModel.IsTemporaryDoubleSpeed);
        CollectionAssert.AreEqual(new[] { 1.5, 2, 1.5 }, context.PlayerService.PlaybackSpeedCalls.Select(call => call.Speed).ToArray());
        Assert.AreEqual(0, context.PlayerService.PauseCallCount);
    }

    [TestMethod]
    public async Task PlaybackSpeed_LateOldInstanceCannotChangeNewMediaDefault()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await context.ViewModel.SetPlaybackSpeedAsync(1.5);
        var oldInstance = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var pending = new TaskCompletionSource<PlayerOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlayerService.PlaybackSpeedHandler = (_, _, _) => pending.Task;
        var press = context.ViewModel.BeginTemporaryDoubleSpeedAsync();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo() with { ItemId = "new-media" });
        Assert.AreNotEqual(oldInstance, context.PlayerService.LastLoadRequest!.PlaybackInstanceId);
        pending.SetResult(PlayerOperationResult.Success());
        await press.WaitAsync(TimeSpan.FromSeconds(3));
        await context.ViewModel.EndTemporaryDoubleSpeedAsync();
        Assert.AreEqual(1d, context.ViewModel.PlaybackSpeed);
        Assert.AreEqual(1d, context.ViewModel.SelectedPlaybackSpeed);
        Assert.IsFalse(context.ViewModel.IsTemporaryDoubleSpeed);
        Assert.IsTrue(context.PlayerService.PlaybackSpeedCalls.All(call => call.Instance == oldInstance));
    }

    [TestMethod]
    public async Task PlaybackSpeed_QualityReloadPreservesSelectedSpeedNotTemporaryBoost()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await context.ViewModel.SetPlaybackSpeedAsync(1.5);
        await context.ViewModel.BeginTemporaryDoubleSpeedAsync();
        var oldInstance = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        ConfigureQualitySuccess(context);
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720).WaitAsync(TimeSpan.FromSeconds(3));
        var currentInstance = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        Assert.AreNotEqual(oldInstance, currentInstance);
        Assert.AreEqual(1.5, context.ViewModel.SelectedPlaybackSpeed);
        Assert.AreEqual(1.5, context.ViewModel.PlaybackSpeed);
        Assert.IsFalse(context.ViewModel.IsTemporaryDoubleSpeed);
        Assert.IsTrue(context.PlayerService.PlaybackSpeedCalls.Contains((currentInstance, 1.5)));
    }

    [TestMethod]
    public async Task PlaybackSpeed_FailureAndExceptionKeepActualValueAndAllowRetry()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.PlaybackSpeedHandler = (_, _, _) => Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackSpeedFailed));
        await context.ViewModel.SetPlaybackSpeedAsync(0.5);
        Assert.AreEqual(1d, context.ViewModel.PlaybackSpeed);
        Assert.AreEqual(0.5, context.ViewModel.SelectedPlaybackSpeed);
        Assert.IsTrue(context.ViewModel.HasPlaybackSpeedError);
        Assert.IsTrue(context.ViewModel.PlaybackSpeedOptions.Single(option => option.Speed == 1).IsSelected);
        Assert.IsNotNull(context.ViewModel.PlayerOptionsMessage);
        context.PlayerService.PlaybackSpeedHandler = (_, _, _) => throw new InvalidOperationException("synthetic native failure");
        await context.ViewModel.BeginTemporaryDoubleSpeedAsync();
        Assert.AreEqual(1d, context.ViewModel.PlaybackSpeed);
        Assert.IsFalse(context.ViewModel.IsTemporaryDoubleSpeed);
        context.PlayerService.PlaybackSpeedHandler = null;
        await context.ViewModel.SetPlaybackSpeedAsync(1.5);
        Assert.AreEqual(1.5, context.ViewModel.PlaybackSpeed);
        Assert.IsNull(context.ViewModel.PlayerOptionsMessage);
        Assert.IsTrue(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task PlaybackSpeed_FailedHoldRestoreKeepsOriginalSelectionAndRetriesTruthfully()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await context.ViewModel.SetPlaybackSpeedAsync(1.5);
        await context.ViewModel.BeginTemporaryDoubleSpeedAsync();
        context.PlayerService.PlaybackSpeedHandler = (_, _, _) => Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackSpeedFailed));
        await context.ViewModel.EndTemporaryDoubleSpeedAsync();
        Assert.AreEqual(2d, context.ViewModel.PlaybackSpeed);
        Assert.AreEqual(1.5, context.ViewModel.SelectedPlaybackSpeed);
        Assert.IsTrue(context.ViewModel.HasPlaybackSpeedError);
        Assert.IsTrue(context.ViewModel.IsSpeedFeedbackVisible);
        StringAssert.Contains(context.ViewModel.SpeedFeedbackText, "2×");
        Assert.IsTrue(context.ViewModel.PlaybackSpeedOptions.Single(option => option.Speed == 2).IsSelected);
        Assert.IsTrue(context.ViewModel.RetryPlaybackSpeedCommand.CanExecute(null));
        context.PlayerService.PlaybackSpeedHandler = null;
        await ((AsyncRelayCommand)context.ViewModel.RetryPlaybackSpeedCommand).ExecuteAsync();
        Assert.AreEqual(1.5, context.ViewModel.PlaybackSpeed);
        Assert.AreEqual(1.5, context.ViewModel.SelectedPlaybackSpeed);
        Assert.IsFalse(context.ViewModel.HasPlaybackSpeedError);
        Assert.IsFalse(context.ViewModel.IsSpeedFeedbackVisible);
        Assert.IsFalse(context.ViewModel.RetryPlaybackSpeedCommand.CanExecute(null));
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.AreEqual(0, context.PlayerService.PauseCallCount);
    }
}
