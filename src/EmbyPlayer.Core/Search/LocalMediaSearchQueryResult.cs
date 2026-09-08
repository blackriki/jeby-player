namespace EmbyPlayer.Core.Search;

public sealed record LocalMediaSearchQueryResult(
    IReadOnlyList<LocalMediaSearchMatch> Matches,
    int TotalIndexedItems,
    TimeSpan? LastBuildDuration,
    TimeSpan? LastLoadDuration,
    string? ApiBase)
{
    public static LocalMediaSearchQueryResult Empty { get; } = new(
        Array.Empty<LocalMediaSearchMatch>(),
        0,
        null,
        null,
        null);
}
