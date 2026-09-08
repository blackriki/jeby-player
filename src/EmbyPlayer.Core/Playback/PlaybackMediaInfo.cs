namespace EmbyPlayer.Core.Playback;

public enum PlaybackMethod
{
    Unknown,
    DirectPlay,
    DirectStream,
    Transcode
}

/// <summary>
/// Source-media metadata returned by Emby, plus the method used by the selected playback path.
/// These dimensions and bitrates describe the source, not the requested transcode limits or MPV output.
/// Missing values remain null.
/// </summary>
public sealed record PlaybackMediaInfo(
    PlaybackMethod Method,
    int? Width = null,
    int? Height = null,
    string? VideoCodec = null,
    long? Bitrate = null,
    long? VideoBitrate = null,
    double? FrameRate = null,
    string? AudioCodec = null,
    int? AudioChannels = null);
