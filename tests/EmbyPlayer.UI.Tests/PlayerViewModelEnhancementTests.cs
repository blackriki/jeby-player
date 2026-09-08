using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task SubtitleAdjustment_AppliesClampsResetsAndKeepsPlaybackRunning()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        await context.ViewModel.AdjustSubtitleAsync("later");
        await context.ViewModel.AdjustSubtitleAsync("larger");
        await context.ViewModel.AdjustSubtitleAsync("up");
        Assert.AreEqual(0.1, context.ViewModel.SubtitleDelaySeconds, 0.001);
        Assert.AreEqual(1.1, context.ViewModel.SubtitleScale, 0.001);
        Assert.AreEqual(95, context.ViewModel.SubtitlePosition);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        for (var i = 0; i < 30; i++) { await context.ViewModel.AdjustSubtitleAsync("smaller"); }
        Assert.AreEqual(0.5, context.ViewModel.SubtitleScale);
        await context.ViewModel.AdjustSubtitleAsync("reset");
        Assert.AreEqual(0, context.ViewModel.SubtitleDelaySeconds);
        Assert.AreEqual(1, context.ViewModel.SubtitleScale);
        Assert.AreEqual(100, context.ViewModel.SubtitlePosition);
    }

    [TestMethod]
    public async Task SubtitleAdjustment_FailureDoesNotClaimAppliedValueOrStopPlayback()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.SubtitleAdjustmentHandler = (_, _, _, _) => Task.FromResult(PlayerOperationResult.Failure(PlayerError.SubtitleAdjustmentFailed));
        var stopCount = context.PlayerService.StopCallCount;
        await context.ViewModel.AdjustSubtitleAsync("later");
        Assert.AreEqual(0, context.ViewModel.SubtitleDelaySeconds);
        Assert.IsNotNull(context.ViewModel.PlayerOptionsMessage);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.AreEqual(stopCount, context.PlayerService.StopCallCount);
    }

    [TestMethod]
    public async Task SubtitleAdjustment_LateResponseCannotChangeReplacementPlayback()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var pending = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SubtitleAdjustmentHandler = (_, _, _, _) => pending.Task;
        var adjust = context.ViewModel.AdjustSubtitleAsync("larger");
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo() with { ItemId = "replacement" }));
        pending.SetResult(PlayerOperationResult.Success());
        await adjust;
        Assert.AreEqual(1, context.ViewModel.SubtitleScale);
        Assert.IsNull(context.ViewModel.PlayerOptionsMessage);
    }

    [TestMethod]
    public async Task PlaybackInformation_SeparatesDecodedResolutionFromSourceMetadata()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context, CreatePlaybackInfo() with
        {
            MediaInfo = new PlaybackMediaInfo(PlaybackMethod.Transcode, 3840, 2160, "hevc", 25_000_000),
            RequiresTranscoding = true
        });
        context.PlayerService.TechnicalInfoHandler = (_, _) => Task.FromResult<PlayerTechnicalInfo?>(new(1280, 720, "h264"));
        await context.ViewModel.RefreshTechnicalInfoAsync();
        Assert.AreEqual("1280 × 720", context.ViewModel.DecodedResolutionText);
        Assert.AreEqual("3840 × 2160", context.ViewModel.SourceResolutionText);
        Assert.AreEqual("H264", context.ViewModel.DecodedVideoCodecText);
        StringAssert.Contains(context.ViewModel.SourceBitrateText, "25");
        Assert.AreEqual("服务器转码", context.ViewModel.PlaybackMethodText);
    }

    [TestMethod]
    public async Task QualitySwitch_PreparationFailureKeepsCurrentStream()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var originalInstance = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        var stopCount = context.PlayerService.StopCallCount;
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => Task.FromResult(PlaybackLoadResult.Failure(PlaybackLoadError.Forbidden));
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);
        Assert.AreEqual(originalInstance, context.PlayerService.LastLoadRequest.PlaybackInstanceId);
        Assert.AreEqual(stopCount, context.PlayerService.StopCallCount);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.IsSwitchingQuality);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "保持原播放");
    }

    [TestMethod]
    public async Task QualitySwitch_UnexpectedCancellationShowsRetryInsteadOfSilentlyDisappearing()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => throw new OperationCanceledException();
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.IsSwitchingQuality);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "请重试");
    }

    [TestMethod]
    public async Task QualitySwitch_PreservesPositionPauseVolumeMuteAndSubtitleAdjustments()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var oldInstance = context.PlayerService.LastLoadRequest!.PlaybackInstanceId;
        context.PlayerService.RaiseProgressChanged(new PlayerProgressChangedEventArgs(oldInstance, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(90), 13.33, false));
        await context.ViewModel.SetVolumeAsync(37);
        await context.ViewModel.ToggleMuteAsync();
        await context.ViewModel.AdjustSubtitleAsync("larger");
        await context.ViewModel.AdjustSubtitleAsync("later");
        context.PlayerService.RaiseStatusChanged(new PlayerStatusChangedEventArgs(oldInstance, PlayerPlaybackState.Paused));
        ConfigureQualitySuccess(context);
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(PlaybackQuality.Hd720, context.ViewModel.PlaybackInfo!.Quality);
        Assert.AreEqual(TimeSpan.FromMinutes(12).Ticks, context.PlayerService.LastLoadRequest!.PlaybackInfo.StartPositionTicks);
        Assert.IsTrue(context.PlayerService.LastLoadRequest.StartPaused);
        Assert.IsTrue(context.ViewModel.IsPaused);
        Assert.AreEqual(37, context.ViewModel.Volume);
        Assert.IsTrue(context.ViewModel.IsMuted);
        Assert.AreEqual(1.1, context.ViewModel.SubtitleScale);
        Assert.AreEqual(0.1, context.ViewModel.SubtitleDelaySeconds);
        Assert.IsFalse(context.ViewModel.IsSwitchingQuality);
        Assert.AreEqual(1, context.PlaybackReportService.StoppedCallCount);
    }

    [TestMethod]
    public async Task QualitySwitch_CancelledByBackNeverLoadsLateResponse()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        var pending = new TaskCompletionSource<PlaybackLoadResult>();
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, _, _) => pending.Task;
        var changing = context.ViewModel.SwitchQualityAsync(PlaybackQuality.FullHd1080);
        await ((AsyncRelayCommand)context.ViewModel.BackCommand).ExecuteAsync();
        var loadCount = context.PlayerService.LoadCallCount;
        pending.SetResult(PlaybackLoadResult.Success(CreatePlaybackInfo() with { Quality = PlaybackQuality.FullHd1080 }));
        await changing;
        Assert.AreEqual(loadCount, context.PlayerService.LoadCallCount);
        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public async Task QualitySwitch_LoadFailureRenegotiatesOriginalAndRestoresIt()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        ConfigureQualitySuccess(context);
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            if (request.PlaybackInfo.Quality == PlaybackQuality.Hd720)
                return Task.FromResult(PlayerOperationResult.Failure(PlayerError.LoadFailed));
            context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Playing));
            return Task.FromResult(PlayerOperationResult.Success());
        };
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(2, context.PlaybackPreparationService.PrepareCallCount);
        Assert.AreEqual(PlaybackQuality.Original, context.ViewModel.PlaybackInfo!.Quality);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "已恢复原画质");
    }

    [TestMethod]
    public async Task QualitySwitch_WaitsForNativeReadyAndRejectsDuplicateRequest()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        ConfigureQualitySuccess(context);
        context.PlayerService.LoadAsyncHandler = (_, _) => Task.FromResult(PlayerOperationResult.Success());
        var changing = context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);
        Assert.IsFalse(changing.IsCompleted);
        Assert.IsTrue(context.ViewModel.IsSwitchingQuality);
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Sd480);
        Assert.AreEqual(1, context.PlaybackPreparationService.PrepareCallCount);
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Playing));
        await changing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(context.ViewModel.IsSwitchingQuality);
        Assert.AreEqual(PlaybackQuality.Hd720, context.ViewModel.PlaybackInfo!.Quality);
    }

    [TestMethod]
    public async Task QualitySwitch_AsynchronousNativeFailureRestoresOriginal()
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
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            PlayerPlaybackState.Failed, PlayerError.LoadFailed));
        await changing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(PlaybackQuality.Original, context.ViewModel.PlaybackInfo!.Quality);
        Assert.IsTrue(context.ViewModel.IsPlaying);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual(2, context.PlaybackReportService.StoppedCallCount);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "已恢复原画质");
    }

    [TestMethod]
    public async Task QualitySwitch_PausedReloadReportsStartedOnlyAfterNativeReady()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Paused));
        ConfigureQualitySuccess(context);
        context.PlayerService.LoadAsyncHandler = (_, _) => Task.FromResult(PlayerOperationResult.Success());
        var starts = context.PlaybackReportService.PlayingCallCount;
        var changing = context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);
        Assert.AreEqual(starts, context.PlaybackReportService.PlayingCallCount);
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId, PlayerPlaybackState.Paused));
        await changing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(starts + 1, context.PlaybackReportService.PlayingCallCount);
        Assert.IsTrue(context.ViewModel.IsPaused);
    }

    [TestMethod]
    public async Task QualitySwitch_ReadyImmediatelyFollowedByFailureDoesNotClaimSuccess()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        ConfigureQualitySuccess(context);
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Playing));
            if (request.PlaybackInfo.Quality == PlaybackQuality.Hd720)
                context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId, PlayerPlaybackState.Failed, PlayerError.LoadFailed));
            return Task.FromResult(PlayerOperationResult.Success());
        };
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(PlaybackQuality.Original, context.ViewModel.PlaybackInfo!.Quality);
        StringAssert.Contains(context.ViewModel.PlayerOptionsMessage!, "已恢复原画质");
    }

    [TestMethod]
    public async Task QualitySwitch_CannotRacePendingAudioSelection()
    {
        var context = CreateContext();
        await StartLoadedPlayerAsync(context);
        RaisePlaying(context, new[]
        {
            new PlayerTrackInfo(8, "audio", "jpn", "Japanese", "aac", false, true, true, false, 1),
            new PlayerTrackInfo(9, "audio", "chi", "Chinese", "aac", false, false, false, false, 2)
        });
        var pending = new TaskCompletionSource<PlayerOperationResult>();
        context.PlayerService.SelectAudioTrackAsyncHandler = (_, _, _) => pending.Task;
        var selecting = context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks[1]);
        Assert.IsFalse(context.ViewModel.CanSwitchQuality);
        await context.ViewModel.SwitchQualityAsync(PlaybackQuality.Hd720);
        Assert.AreEqual(0, context.PlaybackPreparationService.PrepareCallCount);
        pending.SetResult(PlayerOperationResult.Success());
        await selecting;
        Assert.IsTrue(context.ViewModel.CanSwitchQuality);
    }

    private static void ConfigureQualitySuccess(PlayerViewModelTestContext context)
    {
        context.PlaybackPreparationService.PreparePlaybackAsyncHandler = (_, request, _) => Task.FromResult(PlaybackLoadResult.Success(
            CreatePlaybackInfoForRequest(request) with { Quality = request.Quality, SelectedAudioStreamIndex = request.AudioStreamIndex, SelectedSubtitleStreamIndex = request.SubtitleStreamIndex }));
        context.PlayerService.LoadAsyncHandler = (request, _) =>
        {
            context.PlayerService.RaiseStatusChanged(new(request.PlaybackInstanceId,
                request.StartPaused ? PlayerPlaybackState.Paused : PlayerPlaybackState.Playing));
            return Task.FromResult(PlayerOperationResult.Success());
        };
    }
}
