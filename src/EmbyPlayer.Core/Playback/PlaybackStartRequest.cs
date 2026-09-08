namespace EmbyPlayer.Core.Playback;

public sealed record PlaybackStartRequest(
    string ItemId,
    string Title,
    long StartPositionTicks,
    string? MediaType = null,
    int? ProductionYear = null,
    PlaybackQuality Quality = PlaybackQuality.Original,
    string? MediaSourceId = null,
    int? AudioStreamIndex = null,
    int? SubtitleStreamIndex = null);
