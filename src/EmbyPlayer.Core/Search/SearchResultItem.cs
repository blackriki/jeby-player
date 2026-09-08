namespace EmbyPlayer.Core.Search;

public sealed record SearchResultItem(
    string Id,
    string Title,
    string Type,
    int? Year,
    string? PosterUrl,
    double? PlayedPercentage,
    bool IsFavorite = false,
    bool IsPlayed = false,
    SearchMatchInfo? MatchInfo = null);
