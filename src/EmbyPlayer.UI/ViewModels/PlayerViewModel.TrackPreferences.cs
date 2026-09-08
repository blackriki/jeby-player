using EmbyPlayer.Core.Authentication;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private AuthSession? trackPreferenceSession;
    private string? trackPreferenceSeriesId;
    private RememberedTrackPreference? rememberedAudioTrack;
    private RememberedTrackPreference? rememberedSubtitleTrack;

    private void BeginTrackPreferenceScope(PlayerNavigationParameter parameter)
    {
        var session = currentSessionService?.CurrentSession;
        var seriesId = string.Equals(parameter.PlaybackInfo.MediaType, "Episode", StringComparison.OrdinalIgnoreCase)
            ? parameter.PlaybackInfo.SeriesId : null;
        if (!ReferenceEquals(session, trackPreferenceSession)
            || string.IsNullOrWhiteSpace(seriesId)
            || !string.Equals(seriesId, trackPreferenceSeriesId, StringComparison.Ordinal))
        {
            rememberedAudioTrack = null;
            rememberedSubtitleTrack = null;
        }
        trackPreferenceSession = session;
        trackPreferenceSeriesId = seriesId;
    }

    private bool HasCurrentTrackPreferenceScope => trackPreferenceSession is not null
        && ReferenceEquals(trackPreferenceSession, currentSessionService?.CurrentSession)
        && !string.IsNullOrWhiteSpace(trackPreferenceSeriesId)
        && string.Equals(trackPreferenceSeriesId, PlaybackInfo?.SeriesId, StringComparison.Ordinal)
        && string.Equals(PlaybackInfo?.MediaType, "Episode", StringComparison.OrdinalIgnoreCase);

    private void RememberAudioTrackPreference(PlayerAudioTrackViewModel track, long playbackInstanceId)
    {
        if (CanAcceptPlayerEvent(playbackInstanceId) && HasCurrentTrackPreferenceScope)
            rememberedAudioTrack = new(track.Language, track.DisplayTitle, track.Codec);
    }

    private void RememberSubtitleTrackPreference(PlayerSubtitleTrackViewModel track, long playbackInstanceId)
    {
        if (track.LocalFilePath is not null)
        {
            rememberedSubtitleTrack = null;
            return;
        }
        if (CanAcceptPlayerEvent(playbackInstanceId) && HasCurrentTrackPreferenceScope)
            rememberedSubtitleTrack = new(track.Language, track.DisplayTitle, track.Codec, track.IsExternal, track.IsOffOption);
    }

    private PlayerAudioTrackViewModel? FindAudioTrackWithPreferences()
    {
        if (HasCurrentTrackPreferenceScope && rememberedAudioTrack is { } remembered)
        {
            var match = AudioTracks.Where(track => track.MpvTrackId.HasValue)
                .Select(track => (Track: track, Score: MatchRememberedTrack(remembered, track.Language, track.DisplayTitle, track.Codec)))
                .Where(item => item.Score > 0).OrderByDescending(item => item.Score).FirstOrDefault().Track;
            if (match is not null) return match;
        }
        return FindPreferredAudioTrack();
    }

    private PlayerSubtitleTrackViewModel? FindSubtitleTrackWithPreferences()
    {
        if (HasCurrentTrackPreferenceScope && rememberedSubtitleTrack is { } remembered)
        {
            if (remembered.IsOff) return SubtitleTracks.FirstOrDefault(track => track.IsOffOption);
            var match = SubtitleTracks.Where(track => !track.IsOffOption && (track.MpvTrackId.HasValue || track.IsExternal))
                .Select(track => (Track: track, Score: MatchRememberedTrack(remembered, track.Language, track.DisplayTitle, track.Codec, track.IsExternal)))
                .Where(item => item.Score > 0).OrderByDescending(item => item.Score).FirstOrDefault().Track;
            if (match is not null) return match;
        }
        return playerPreferences.DefaultSubtitlesEnabled
            ? FindPreferredSubtitleTrack() : SubtitleTracks.FirstOrDefault(track => track.IsOffOption);
    }

    private static int MatchRememberedTrack(RememberedTrackPreference remembered,
        string language, string title, string codec, bool? isExternal = null)
    {
        var sameTitle = !string.IsNullOrWhiteSpace(remembered.Title)
            && string.Equals(remembered.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase);
        var hasLanguage = !IsAutomaticLanguagePreference(remembered.Language)
            && !string.Equals(remembered.Language, "und", StringComparison.OrdinalIgnoreCase);
        if (hasLanguage)
        {
            if (!MatchesPreferredLanguage(new[] { language }, remembered.Language, MatchStrength.Exact)
                && !MatchesPreferredLanguage(new[] { language }, remembered.Language, MatchStrength.Alias)) return 0;
        }
        else if (!sameTitle) return 0;

        return 1 + (sameTitle ? 8 : 0)
            + (!string.IsNullOrWhiteSpace(remembered.Codec)
                && string.Equals(remembered.Codec, codec, StringComparison.OrdinalIgnoreCase) ? 4 : 0)
            + (remembered.IsExternal.HasValue && remembered.IsExternal == isExternal ? 2 : 0);
    }

    private sealed record RememberedTrackPreference(string Language, string Title, string Codec,
        bool? IsExternal = null, bool IsOff = false);
}
