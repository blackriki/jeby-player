namespace EmbyPlayer.Core.Series;

public sealed record EpisodeInfo(
    string Id,
    string Title,
    int? SeasonIndex,
    int? EpisodeIndex,
    long? RunTimeTicks,
    string? Overview,
    double? PlayedPercentage,
    long? ResumePositionTicks,
    string? ThumbnailUrl,
    bool IsPlayed = false,
    string? HeroImageUrl = null);
