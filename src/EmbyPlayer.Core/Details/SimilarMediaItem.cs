namespace EmbyPlayer.Core.Details;

public sealed record SimilarMediaItem(
    string Id,
    string Title,
    string Type,
    int? Year,
    string? PosterUrl,
    double? PlayedPercentage,
    long? ResumePositionTicks,
    bool IsPlayed);
