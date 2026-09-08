namespace EmbyPlayer.Core.Details;

public sealed record MediaDetail
{
    public MediaDetail(
        string id,
        string title,
        string type,
        int? year,
        long? runTimeTicks,
        string? overview,
        IReadOnlyList<string>? genres,
        double? communityRating,
        double? playedPercentage,
        long? resumePositionTicks,
        string? posterUrl,
        string? backdropUrl,
        bool isFavorite = false,
        bool isPlayed = false,
        string? logoUrl = null,
        IReadOnlyList<MediaPerson>? people = null,
        IReadOnlyList<string>? artworkUrls = null)
    {
        Id = id;
        Title = title;
        Type = type;
        Year = year;
        RunTimeTicks = runTimeTicks;
        Overview = overview;
        Genres = genres ?? Array.Empty<string>();
        CommunityRating = communityRating;
        PlayedPercentage = playedPercentage;
        ResumePositionTicks = resumePositionTicks;
        PosterUrl = posterUrl;
        BackdropUrl = backdropUrl;
        IsFavorite = isFavorite;
        IsPlayed = isPlayed;
        LogoUrl = logoUrl;
        People = people ?? Array.Empty<MediaPerson>();
        ArtworkUrls = artworkUrls ?? Array.Empty<string>();
    }

    public string Id { get; init; }

    public string Title { get; init; }

    public string Type { get; init; }

    public int? Year { get; init; }

    public long? RunTimeTicks { get; init; }

    public string? Overview { get; init; }

    public IReadOnlyList<string> Genres { get; init; }

    public double? CommunityRating { get; init; }

    public double? PlayedPercentage { get; init; }

    public long? ResumePositionTicks { get; init; }

    public string? PosterUrl { get; init; }

    public string? BackdropUrl { get; init; }

    public bool IsFavorite { get; init; }

    public bool IsPlayed { get; init; }

    public string? LogoUrl { get; init; }

    public IReadOnlyList<MediaPerson> People { get; init; }

    public IReadOnlyList<string> ArtworkUrls { get; init; }
}
