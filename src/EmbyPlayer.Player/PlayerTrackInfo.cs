namespace EmbyPlayer.Player;

public sealed record PlayerTrackInfo(
    int Id,
    string Type,
    string? Language,
    string? Title,
    string? Codec,
    bool? IsExternal,
    bool? IsSelected,
    bool? IsDefault,
    bool? IsForced,
    int? MediaStreamIndex = null,
    string? LocalFilePath = null);
