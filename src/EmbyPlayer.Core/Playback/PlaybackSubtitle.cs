namespace EmbyPlayer.Core.Playback;

public sealed record PlaybackSubtitle(
    int Index,
    string Language,
    string Codec,
    string DisplayTitle,
    bool IsDefault,
    bool IsExternal,
    string? DeliveryMethod,
    string? DeliveryUrl = null);
