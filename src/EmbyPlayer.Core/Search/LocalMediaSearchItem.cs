namespace EmbyPlayer.Core.Search;

public sealed record LocalMediaSearchItem(
    string Id,
    string Name,
    string? OriginalTitle,
    string? SortName,
    string? SeriesName,
    string Type,
    int? ProductionYear,
    string? PrimaryImageTag,
    int? IndexNumber,
    int? ParentIndexNumber);
