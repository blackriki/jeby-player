namespace EmbyPlayer.Player;

internal readonly record struct MpvEventSnapshot(
    MpvEventId EventId,
    int Error,
    int EndFileReason = 0,
    int EndFileError = 0,
    string? LogMessageKeywords = null);
