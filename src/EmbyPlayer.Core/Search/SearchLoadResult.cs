namespace EmbyPlayer.Core.Search;

public sealed class SearchLoadResult
{
    private SearchLoadResult(
        bool isSuccess,
        IReadOnlyList<SearchResultItem>? items,
        SearchLoadError error,
        SearchExecutionDiagnostics? diagnostics)
    {
        IsSuccess = isSuccess;
        Items = items;
        Error = error;
        Diagnostics = diagnostics;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<SearchResultItem>? Items { get; }

    public SearchLoadError Error { get; }

    public SearchExecutionDiagnostics? Diagnostics { get; }

    public static SearchLoadResult Success(
        IReadOnlyList<SearchResultItem> items,
        SearchExecutionDiagnostics? diagnostics = null)
    {
        return new SearchLoadResult(true, items, SearchLoadError.None, diagnostics);
    }

    public static SearchLoadResult Failure(SearchLoadError error)
    {
        return new SearchLoadResult(false, null, error, null);
    }
}
