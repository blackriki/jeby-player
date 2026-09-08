namespace EmbyPlayer.Core.Home;

public sealed class MediaCard
{
    public MediaCard(
        string id,
        string title,
        string type,
        int? year,
        string? posterUrl,
        double? playedPercentage,
        string? subtitle = null,
        bool isFavorite = false,
        bool isPlayed = false,
        string? heroImageUrl = null,
        long? resumePositionTicks = null,
        DateTimeOffset? dateCreated = null,
        string? seriesId = null,
        string? seasonId = null,
        string? logoUrl = null)
    {
        Id = id ?? string.Empty;
        Title = title ?? string.Empty;
        Type = type ?? string.Empty;
        Year = year;
        PosterUrl = posterUrl;
        HeroImageUrl = string.IsNullOrWhiteSpace(heroImageUrl) ? posterUrl : heroImageUrl;
        PlayedPercentage = playedPercentage;
        Subtitle = subtitle ?? string.Empty;
        IsFavorite = isFavorite;
        IsPlayed = isPlayed;
        ResumePositionTicks = resumePositionTicks;
        DateCreated = dateCreated;
        SeriesId = seriesId;
        SeasonId = seasonId;
        LogoUrl = logoUrl;
    }

    public string Id { get; }

    public string Title { get; }

    public string Type { get; }

    public int? Year { get; }

    public string? PosterUrl { get; }

    public string? HeroImageUrl { get; }

    public double? PlayedPercentage { get; }

    public string Subtitle { get; }

    public bool IsFavorite { get; }

    public bool IsPlayed { get; }

    public long? ResumePositionTicks { get; }

    public DateTimeOffset? DateCreated { get; }

    public string? SeriesId { get; }

    public string? SeasonId { get; }

    public string? LogoUrl { get; }
}
