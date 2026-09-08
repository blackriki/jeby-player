namespace EmbyPlayer.Core.Playback;

public sealed record PlaybackMediaSource(
    string Id,
    string? Container,
    bool SupportsDirectPlay,
    bool SupportsDirectStream,
    bool SupportsTranscoding,
    IReadOnlyDictionary<string, string> RequiredHttpHeaders);
