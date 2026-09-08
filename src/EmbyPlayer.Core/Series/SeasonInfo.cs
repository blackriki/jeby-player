namespace EmbyPlayer.Core.Series;

public sealed record SeasonInfo(
    string Id,
    string Name,
    int? IndexNumber,
    bool HasResumeOrUnwatched);
