using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class PlayerViewModelTests
{
    [TestMethod]
    public async Task TrackPreferences_DefaultOffDisablesSelectedSubtitleAndSameSeriesManualChoicesOverrideDefaults()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        { DefaultSubtitlesEnabled = false, DefaultSubtitleLanguage = "chi", DefaultAudioLanguage = "jpn" };
        await LoadTrackPreferenceEpisodeAsync(context, "one", "series-a", new[]
        {
            Track(1, "audio", "jpn", "Original", "aac", selected: true),
            Track(2, "audio", "eng", "Main", "ac3"),
            Track(3, "audio", "eng", "Commentary", "ac3"),
            Track(10, "sub", "chi", "Chinese", "srt", selected: true),
            Track(11, "sub", "eng", "English styled", "ass", external: true)
        });
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.IsOffOption).IsSelected);
        Assert.AreEqual(1, context.PlayerService.DisableSubtitleCallCount);
        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 3));
        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 11));

        await LoadTrackPreferenceEpisodeAsync(context, "two", "series-a", new[]
        {
            Track(3, "audio", "jpn", "Original", "aac", selected: true),
            Track(7, "audio", "eng", "Commentary", "aac"),
            Track(8, "audio", "eng", "Main", "ac3"),
            Track(9, "audio", "eng", "Commentary", "ac3"),
            Track(11, "sub", "chi", "Chinese", "srt", selected: true),
            Track(12, "sub", "eng", "English styled", "ass", external: true),
            Track(13, "sub", "eng", "English styled", "srt")
        });
        Assert.AreEqual(9, context.PlayerService.LastAudioTrackIndex);
        Assert.AreEqual(12, context.PlayerService.LastSubtitleTrackIndex);
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 12).IsSelected);
        Assert.IsFalse(context.AppSettingsService.PlayerPreferences.DefaultSubtitlesEnabled,
            "Per-series choices must not rewrite the saved defaults.");
    }

    [TestMethod]
    public async Task TrackPreferences_SubtitleOffIsRememberedAcrossEpisodesButMissingAudioFallsBackToLanguagePreference()
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with
        { DefaultSubtitleLanguage = "chi", DefaultAudioLanguage = "chi" };
        await LoadTrackPreferenceEpisodeAsync(context, "one", "series-a", new[]
        {
            Track(1, "audio", "chi", "Chinese", "aac", selected: true),
            Track(2, "audio", "eng", "English", "aac"),
            Track(3, "sub", "chi", "Chinese", "srt", selected: true)
        });
        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 2));
        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks.Single(track => track.IsOffOption));
        await LoadTrackPreferenceEpisodeAsync(context, "two", "series-a", new[]
        {
            Track(1, "audio", "jpn", "Japanese", "aac", selected: true),
            Track(4, "audio", "chi", "Chinese", "aac"),
            Track(5, "sub", "chi", "Chinese", "srt", selected: true)
        });
        Assert.AreEqual(4, context.PlayerService.LastAudioTrackIndex);
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.IsOffOption).IsSelected);
        Assert.AreEqual(2, context.PlayerService.DisableSubtitleCallCount);
    }

    [DataTestMethod]
    [DataRow("series")]
    [DataRow("movie")]
    [DataRow("missing-series")]
    [DataRow("session")]
    public async Task TrackPreferences_DoNotCrossSeriesMovieOrSessionBoundaries(string boundary)
    {
        var context = CreateContext();
        context.AppSettingsService.PlayerPreferences = PlayerPreferences.Default with { DefaultAudioLanguage = "jpn" };
        var tracks = new[]
        {
            Track(1, "audio", "jpn", "Original", "aac", selected: true),
            Track(2, "audio", "eng", "English", "aac")
        };
        await LoadTrackPreferenceEpisodeAsync(context, "one", "series-a", tracks);
        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 2));
        if (boundary == "session") context.CurrentSessionService.SetSession(new AuthSession(
            context.Session.ServerBase, "replacement-token", "other-user", "Other user", "server-1"));
        await LoadTrackPreferenceEpisodeAsync(context, "two",
            boundary == "series" ? "series-b" : boundary == "missing-series" ? null : "series-a", tracks,
            boundary == "movie" ? "Movie" : "Episode");
        Assert.IsTrue(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 1).IsSelected);
    }

    [TestMethod]
    public async Task TrackPreferences_ExplicitlyConfirmingCurrentTracksSurvivesRefreshAndCarriesToNextEpisode()
    {
        var context = CreateContext();
        await LoadTrackPreferenceEpisodeAsync(context, "one", "series-a", new[]
        {
            Track(1, "audio", "eng", "English", "aac", selected: true),
            Track(2, "sub", "eng", "English", "srt", selected: true)
        });
        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 1));
        await context.ViewModel.SelectSubtitleTrackAsync(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 2));
        RaisePlaying(context, new[]
        {
            Track(1, "audio", "eng", "English", "aac"),
            Track(2, "sub", "eng", "English", "srt")
        });
        Assert.IsTrue(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 1).IsSelected);
        Assert.IsTrue(context.ViewModel.SubtitleTracks.Single(track => track.MpvTrackId == 2).IsSelected);
        await LoadTrackPreferenceEpisodeAsync(context, "two", "series-a", new[]
        {
            Track(1, "audio", "jpn", "Japanese", "aac", selected: true),
            Track(3, "audio", "eng", "English", "aac"),
            Track(2, "sub", "chi", "Chinese", "srt", selected: true),
            Track(4, "sub", "eng", "English", "srt")
        });
        Assert.AreEqual(3, context.PlayerService.LastAudioTrackIndex);
        Assert.AreEqual(4, context.PlayerService.LastSubtitleTrackIndex);
    }

    [TestMethod]
    public async Task TrackPreferences_FailedManualChoiceDoesNotReplaceEarlierSuccessfulChoice()
    {
        var context = CreateContext();
        var tracks = new[]
        {
            Track(1, "audio", "jpn", "Original", "aac", selected: true),
            Track(2, "audio", "eng", "English", "aac"),
            Track(3, "audio", "chi", "Chinese", "aac")
        };
        await LoadTrackPreferenceEpisodeAsync(context, "one", "series-a", tracks);
        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 2));
        context.PlayerService.SelectAudioTrackAsyncHandler = (_, _, _) => Task.FromResult(PlayerOperationResult.Failure(PlayerError.AudioTrackFailed));
        await context.ViewModel.SelectAudioTrackAsync(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 3));
        context.PlayerService.SelectAudioTrackAsyncHandler = null;
        await LoadTrackPreferenceEpisodeAsync(context, "two", "series-a", tracks);
        Assert.IsTrue(context.ViewModel.AudioTracks.Single(track => track.MpvTrackId == 2).IsSelected);
    }

    private static PlayerTrackInfo Track(int id, string type, string language, string title, string codec,
        bool selected = false, bool external = false) => new(id, type, language, title, codec, external, selected, selected, false, id + 100);

    private static async Task LoadTrackPreferenceEpisodeAsync(PlayerViewModelTestContext context, string itemId,
        string? seriesId, IReadOnlyList<PlayerTrackInfo> tracks, string type = "Episode")
    {
        context.ViewModel.Load(CreateNavigationParameter(CreatePlaybackInfo() with { ItemId = itemId, MediaType = type, SeriesId = seriesId }));
        await context.ViewModel.AttachVideoHostAsync(new IntPtr(1234));
        RaisePlaying(context, tracks);
        await Task.Delay(50);
    }
}
