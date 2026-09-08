namespace EmbyPlayer.Core.Playback;

public sealed record PlaybackTrack(
    int Index,
    string Language,
    string Codec,
    string DisplayTitle,
    bool IsDefault);
