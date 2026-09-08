using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task LocalSubtitleVm_ImportSelectsLocalWithoutReportingServerStreamIndex()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.LocalSubtitleHandler = (_, path, _) => Task.FromResult(LocalSubtitleResult(path));
        await context.ViewModel.ImportLocalSubtitleAsync("fixture.srt");
        var selected = context.ViewModel.SubtitleTracks.Single(track => track.IsSelected);
        Assert.AreEqual("fixture.srt", selected.LocalFilePath);
        Assert.IsNull(selected.MediaStreamIndex);
        StringAssert.Contains(selected.DisplayText, "本地");
        Assert.IsTrue(context.ViewModel.IsPlaying);
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        Assert.IsNull(context.PlaybackReportService.LastStoppedRequest!.SubtitleStreamIndex);
    }

    [TestMethod]
    public async Task LocalSubtitleVm_FailureKeepsSelectedSubtitleAndPausedPlayback()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.LocalSubtitleHandler = (_, path, _) => Task.FromResult(LocalSubtitleResult(path));
        await context.ViewModel.ImportLocalSubtitleAsync("original.ass");
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Paused));
        context.PlayerService.LocalSubtitleHandler = (_, _, _) => Task.FromResult(new LocalSubtitleImportResult(false, []));
        await context.ViewModel.ImportLocalSubtitleAsync("unreadable.srt");
        Assert.AreEqual("original.ass", context.ViewModel.SubtitleTracks.Single(track => track.IsSelected).LocalFilePath);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.IsTrue(context.ViewModel.CanImportLocalSubtitle);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "加载失败");
    }

    [TestMethod]
    public async Task LocalSubtitleVm_ObsoleteImportCannotSelectSubtitleInNewMedia()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var pending = new TaskCompletionSource<LocalSubtitleImportResult>();
        context.PlayerService.LocalSubtitleHandler = (_, _, _) => pending.Task;
        var importing = context.ViewModel.ImportLocalSubtitleAsync("old.srt");
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo() with { ItemId = "second-item" });
        pending.SetResult(LocalSubtitleResult("old.srt"));
        await importing;
        Assert.AreEqual("second-item", context.ViewModel.PlaybackInfo!.ItemId);
        Assert.IsFalse(context.ViewModel.SubtitleTracks.Any(track => track.LocalFilePath is not null));
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsTrue(context.ViewModel.CanImportLocalSubtitle);
    }

    [TestMethod]
    public async Task LocalSubtitleVm_QualityReloadAndPlaybackRetryReimportSelectedPath()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var imports = new List<(long Instance, string Path)>();
        context.PlayerService.LocalSubtitleHandler = (instance, path, _) =>
        {
            imports.Add((instance, path));
            return Task.FromResult(LocalSubtitleResult(path));
        };
        await context.ViewModel.ImportLocalSubtitleAsync("retained.ass");
        ConfigureQualitySuccess(context);
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(2, imports.Count);
        Assert.AreNotEqual(imports[0].Instance, imports[1].Instance);
        Assert.AreEqual("retained.ass", context.ViewModel.SubtitleTracks.Single(track => track.IsSelected).LocalFilePath);
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        await context.ViewModel.RetryPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(3, imports.Count);
        Assert.AreNotEqual(imports[1].Instance, imports[2].Instance);
        Assert.IsTrue(imports.All(import => import.Path == "retained.ass"));
        Assert.AreEqual("retained.ass", context.ViewModel.SubtitleTracks.Single(track => track.IsSelected).LocalFilePath);
        Assert.IsFalse(context.ViewModel.HasError);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalSubtitleVm_ReloadWaitsForNativeReadyBeforeImportAndAwaitsRestore(bool paused)
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.LocalSubtitleHandler = (_, path, _) => Task.FromResult(LocalSubtitleResult(path));
        await context.ViewModel.ImportLocalSubtitleAsync("delayed.ass");
        if (paused)
            context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
                PlayerPlaybackState.Paused));
        ConfigureQualitySuccess(context);
        var loadAccepted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreCompletion = new TaskCompletionSource<LocalSubtitleImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            loadAccepted.TrySetResult(request.PlaybackInstanceId);
            return Task.FromResult(PlayerOperationResult.Success());
        };
        context.PlayerService.LocalSubtitleHandler = (_, path, _) =>
        {
            restoreStarted.TrySetResult(path);
            return restoreCompletion.Task;
        };

        var switching = context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);
        var instance = await loadAccepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // The command was accepted, but MPV has not announced FileLoaded/Playing yet.
        Assert.IsFalse(restoreStarted.Task.IsCompleted);
        Assert.IsFalse(switching.IsCompleted);
        context.PlayerService.RaiseStatusChanged(new(instance, paused ? PlayerPlaybackState.Paused : PlayerPlaybackState.Playing));
        Assert.AreEqual("delayed.ass", await restoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsFalse(switching.IsCompleted);
        Assert.IsTrue(context.ViewModel.IsSwitchingQuality);
        restoreCompletion.SetResult(LocalSubtitleResult("delayed.ass"));
        await switching.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(context.ViewModel.IsSwitchingQuality);
        Assert.AreEqual("delayed.ass", context.ViewModel.SubtitleTracks.Single(track => track.IsSelected).LocalFilePath);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual(paused, context.ViewModel.IsPaused);
    }

    private static LocalSubtitleImportResult LocalSubtitleResult(string path) => new(true,
        [new PlayerTrackInfo(15, "sub", "chi", "Local caption", "srt", true, true, false, false, null, path)]);
}
