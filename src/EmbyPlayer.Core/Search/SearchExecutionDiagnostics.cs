namespace EmbyPlayer.Core.Search;

public sealed record SearchExecutionDiagnostics(
    int ServerResultCount,
    int LocalIndexResultCount,
    int MergedResultCount,
    int TotalIndexedItems,
    TimeSpan? IndexBuildDuration,
    TimeSpan? IndexLoadDuration);
