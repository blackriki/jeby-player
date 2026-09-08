namespace EmbyPlayer.Core.Series;

public sealed record NextEpisodeInfo(
    string ItemId,
    string Title,
    int SeasonNumber,
    int EpisodeNumber,
    long? RunTimeTicks,
    string? LogoUrl = null,
    string? SeriesId = null,
    string? SeasonId = null);
