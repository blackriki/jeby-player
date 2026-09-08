using System.Windows.Input;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task RepeatedTrackShortcut_DoesNotQueueDuplicatePendingNativeOperations()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "eng", "English", "aac", false, true, true, false, 1),
            new PlayerTrackInfo(9, "audio", "chi", "Chinese", "aac", false, false, false, false, 2)
        });
        var pending = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SelectAudioTrackAsyncHandler = (_, _, _) => pending.Task;
        var first = context.ViewModel.HandleShortcutKeyAsync(Key.A, ModifierKeys.None);
        var calls = context.PlayerService.SelectAudioTrackCallCount;
        await context.ViewModel.HandleShortcutKeyAsync(Key.A, ModifierKeys.None);
        Assert.AreEqual(calls, context.PlayerService.SelectAudioTrackCallCount);
        pending.SetResult(PlayerOperationResult.Success());
        await first;
    }

    [TestMethod]
    public async Task CustomShortcut_RequiresExactModifiersAndRemovesPreviousBinding()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        { Shortcuts = PlayerShortcutBindings.Default with { ToggleMute = "Ctrl+Shift+K" } };
        await StartLoadedPlayerAsync(context);
        Assert.IsFalse(await context.ViewModel.HandleShortcutKeyAsync(Key.M, ModifierKeys.None));
        Assert.IsFalse(await context.ViewModel.HandleShortcutKeyAsync(Key.K, ModifierKeys.Control));
        Assert.IsFalse(await context.ViewModel.HandleShortcutKeyAsync(Key.K, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        var before = context.PlayerService.SetMuteCallCount;
        Assert.IsTrue(await context.ViewModel.HandleShortcutKeyAsync(Key.K, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.AreEqual(before + 1, context.PlayerService.SetMuteCallCount);
        StringAssert.Contains(context.ViewModel.ShortcutHelpText, "Ctrl+Shift+K");
    }

    [TestMethod]
    public async Task SubtitleDelayShortcut_DoesNotTriggerSeekAndRejectsSystemChords()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var seekCount = context.PlayerService.SeekCallCount;
        Assert.IsTrue(await context.ViewModel.HandleShortcutKeyAsync(Key.Right, ModifierKeys.Control));
        Assert.AreEqual(0.1, context.ViewModel.SubtitleDelaySeconds, 0.001);
        Assert.AreEqual(seekCount, context.PlayerService.SeekCallCount);
        Assert.IsFalse(await context.ViewModel.HandleShortcutKeyAsync(Key.F4, ModifierKeys.Alt));
        Assert.IsNull(context.ViewModel.ResolveShortcut(Key.Space, ModifierKeys.Alt));
        Assert.AreEqual(PlayerShortcutAction.ToggleFullscreen, context.ViewModel.ResolveShortcut(Key.F, ModifierKeys.None));
    }

    [TestMethod]
    public async Task TrackShortcuts_CycleLoadedTracksIncludingSubtitleOff()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "eng", "English", "aac", false, true, true, false, 1),
            new PlayerTrackInfo(9, "audio", "chi", "Chinese", "aac", false, false, false, false, 2),
            new PlayerTrackInfo(10, "sub", "eng", "English", "srt", false, true, true, false, 3),
            new PlayerTrackInfo(11, "sub", "chi", "Chinese", "srt", false, false, false, false, 4)
        });
        var audioCalls = context.PlayerService.SelectAudioTrackCallCount;
        await context.ViewModel.HandleShortcutKeyAsync(Key.A, ModifierKeys.None);
        Assert.AreEqual(audioCalls + 1, context.PlayerService.SelectAudioTrackCallCount);
        Assert.IsTrue(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 9).IsSelected);
        await context.ViewModel.HandleShortcutKeyAsync(Key.J, ModifierKeys.None);
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 11).IsSelected);
        await context.ViewModel.HandleShortcutKeyAsync(Key.J, ModifierKeys.None);
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.IsOffOption).IsSelected);
    }
}
