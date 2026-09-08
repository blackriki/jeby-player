namespace EmbyPlayer.Core.Playback;

/// <param name="StreamPositionOffsetTicks">
/// Confirmed server-stream timeline offset. Convert source positions to player positions by
/// subtracting it, and add it when reporting playback progress. Zero is the default full-media timeline.
/// </param>
public sealed record PlaybackInfo(
    string ItemId,
    string Title,
    string? PlaySessionId,
    PlaybackMediaSource MediaSource,
    string? PlaybackPath,
    bool RequiresTranscoding,
    bool RequiresTokenInUrl,
    long? RunTimeTicks,
    long StartPositionTicks,
    IReadOnlyList<PlaybackTrack> AudioTracks,
    IReadOnlyList<PlaybackSubtitle> Subtitles,
    string? MediaType = null,
    int? ProductionYear = null,
    IReadOnlyList<PlaybackMarker>? Markers = null,
    PlaybackQuality Quality = PlaybackQuality.Original,
    int? SelectedAudioStreamIndex = null,
    int? SelectedSubtitleStreamIndex = null,
    PlaybackMediaInfo? MediaInfo = null,
    long StreamPositionOffsetTicks = 0,
    string? SeriesId = null);
