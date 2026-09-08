using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DefaultSubtitle_WaitsForNativeReadyAndLoadsExternalEvenWhenServerMarksDefault(bool serverDefault, bool paused)
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        { DefaultSubtitlesEnabled = true, DefaultSubtitleLanguage = "chi" };
        var nativeReady = false;
        var nativeCalls = 0;
        context.PlayerService.SelectExternalSubtitleAsyncHandler = (_, _, _) =>
        {
            nativeCalls++;
            return Task.FromResult(nativeReady ? PlayerOperationResult.Success()
                : PlayerOperationResult.Failure(PlayerError.SubtitleTrackFailed));
        };
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo(subtitles:
            [new PlaybackSubtitle(4, "chi", "srt", "Chinese", serverDefault, true, "External", "http://fixture.invalid/sub.srt")])));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        Assert.AreEqual(0, nativeCalls, "Load acceptance is not native readiness.");
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.IsOffOption).IsSelected);
        nativeReady = true;
        context.PlayerService.RaiseStatusChanged(new(context.PlayerService.LastLoadRequest!.PlaybackInstanceId,
            paused ? PlayerPlaybackState.Paused : PlayerPlaybackState.Playing));
        Assert.AreEqual(1, nativeCalls);
        Assert.AreEqual(4, context.ViewModel.SubtitleTracks.Single(track => track.IsSelected).MediaStreamIndex);
        Assert.IsFalse(context.ViewModel.HasError);
    }

    [TestMethod]
    public async Task DefaultSubtitle_MissingEarlyTrackListDoesNotConsumeLanguageSelection()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        { DefaultSubtitlesEnabled = true, DefaultSubtitleLanguage = "chi" };
        await StartLoadedPlayerAsync(context);
        Assert.AreEqual(0, context.PlayerService.SelectSubtitleTrackCallCount);
        RaisePlaying(context, [Track(4, "sub", "chi", "Chinese", "srt")]);
        Assert.AreEqual(4, context.PlayerService.LastSubtitleTrackIndex);
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 4).IsSelected);
    }
}
