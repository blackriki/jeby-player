namespace EmbyPlayer.Core.Library;

public sealed record LibraryMediaItem(
    string Id,
    string Title,
    string Type,
    int? Year,
    string? PosterUrl,
    double? PlayedPercentage,
    bool IsFavorite = false,
    bool IsPlayed = false);
