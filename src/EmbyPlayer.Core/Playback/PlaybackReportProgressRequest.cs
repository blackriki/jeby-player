namespace EmbyPlayer.Core.Playback;

public sealed record PlaybackReportProgressRequest(
    string ItemId,
    string? MediaSourceId,
    string? PlaySessionId,
    long PositionTicks,
    long? RunTimeTicks,
    bool IsPaused,
    bool CanSeek,
    string PlayMethod,
    bool IsMuted = false,
    int? VolumeLevel = null,
    int? AudioStreamIndex = null,
    int? SubtitleStreamIndex = null);
