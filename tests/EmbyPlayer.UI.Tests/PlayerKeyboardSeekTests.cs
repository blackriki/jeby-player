using EmbyPlayer.Core.Settings;
using EmbyPlayer.Player;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task KeyboardSeek_TapUsesConfiguredStepAndCommitsOnlyOnRelease()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { SeekSeconds = 15 };
        await StartLoadedPlayerAsync(context);
        Assert.IsTrue(context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward));
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromMilliseconds(350));
        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
        StringAssert.Contains(context.ViewModel.KeyboardSeekFeedbackText, "00:15");
        await context.ViewModel.CompleteKeyboardSeekAsync();
        await context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        Assert.AreEqual(TimeSpan.FromSeconds(15), context.PlayerService.LastSeekPosition);
        Assert.IsFalse(context.ViewModel.IsKeyboardSeekActive);
        Assert.IsTrue(context.ViewModel.IsPlaying);
    }

    [TestMethod]
    public async Task KeyboardSeek_HoldAcceleratesPreviewWithoutNativeSeeks()
    {
        var context = CreateContext(); await StartLoadedPlayerAsync(context);
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward);
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(1.4));
        var early = context.ViewModel.SeekPercent;
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(2.4));
        var middle = context.ViewModel.SeekPercent;
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(5.4));
        var late = context.ViewModel.SeekPercent;
        Assert.IsTrue(middle > early && (late - middle) / 3 > middle - early);
        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
        await context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        Assert.AreEqual(160, context.PlayerService.LastSeekPosition.TotalSeconds, 0.001);
    }

    [TestMethod]
    public async Task KeyboardSeek_ClampsBothEndsAndPreservesPausedState()
    {
        var context = CreateContext(); await StartLoadedPlayerAsync(context);
        await ((AsyncRelayCommand)context.ViewModel.PauseCommand).ExecuteAsync();
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekBackward);
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(1000));
        await context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.AreEqual(TimeSpan.Zero, context.PlayerService.LastSeekPosition);
        Assert.IsTrue(context.ViewModel.IsPaused);
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward);
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(1000));
        await context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(90), context.PlayerService.LastSeekPosition);
        Assert.IsTrue(context.ViewModel.IsPaused);
    }

    [TestMethod]
    public async Task KeyboardSeek_CancelAndOppositeDirectionDoNotCommitOldTarget()
    {
        var context = CreateContext(); await StartLoadedPlayerAsync(context);
        await context.ViewModel.SeekRelativeAsync(TimeSpan.FromSeconds(100));
        var calls = context.PlayerService.SeekCallCount;
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward);
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(8));
        context.ViewModel.CancelKeyboardSeek();
        Assert.AreEqual(context.ViewModel.ProgressPercent, context.ViewModel.SeekPercent);
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward);
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(8));
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekBackward);
        await context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.AreEqual(calls + 1, context.PlayerService.SeekCallCount);
        Assert.AreEqual(90, context.PlayerService.LastSeekPosition.TotalSeconds, 0.001);
    }

    [TestMethod]
    public async Task KeyboardSeek_PendingCommitRejectsAdditionalPressesAndFailureRecovers()
    {
        var context = CreateContext(); await StartLoadedPlayerAsync(context);
        var pending = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SeekAsyncHandler = (_, _, _) => pending.Task;
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward);
        var commit = context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.IsFalse(context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward));
        await context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
        pending.SetResult(PlayerOperationResult.Failure(PlayerError.SeekFailed)); await commit;
        Assert.IsFalse(context.ViewModel.IsSeeking);
        Assert.IsFalse(context.ViewModel.HasError);
        StringAssert.Contains(context.ViewModel.StatusText, "跳转失败");
        Assert.IsTrue(context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward));
        context.ViewModel.CancelKeyboardSeek();
    }

    [TestMethod]
    public async Task KeyboardSeek_StaleReleaseCannotSeekOrCancelNewItemPointerDrag()
    {
        var context = CreateContext(); await StartLoadedPlayerAsync(context);
        context.ViewModel.BeginKeyboardSeek(PlayerShortcutAction.SeekForward);
        context.ViewModel.UpdateKeyboardSeek(TimeSpan.FromSeconds(5));
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo() with { ItemId = "new-item" });
        context.ViewModel.BeginSeekDrag(); context.ViewModel.UpdateSeekDrag(40);
        await context.ViewModel.CompleteKeyboardSeekAsync();
        Assert.AreEqual(0, context.PlayerService.SeekCallCount);
        Assert.AreEqual(40, context.ViewModel.SeekPercent, 0.001);
        await context.ViewModel.CompleteSeekDragAsync(40);
        Assert.AreEqual(1, context.PlayerService.SeekCallCount);
    }
}
