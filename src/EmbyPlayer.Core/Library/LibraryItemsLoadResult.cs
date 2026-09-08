namespace EmbyPlayer.Core.Library;

public sealed class LibraryItemsLoadResult
{
    private LibraryItemsLoadResult(
        bool isSuccess,
        IReadOnlyList<LibraryMediaItem>? items,
        int? totalRecordCount,
        LibraryLoadError error)
    {
        IsSuccess = isSuccess;
        Items = items ?? Array.Empty<LibraryMediaItem>();
        TotalRecordCount = totalRecordCount;
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<LibraryMediaItem> Items { get; }

    public int? TotalRecordCount { get; }

    public LibraryLoadError Error { get; }

    public static LibraryItemsLoadResult Success(
        IReadOnlyList<LibraryMediaItem> items,
        int? totalRecordCount = null)
    {
        return new LibraryItemsLoadResult(true, items, totalRecordCount, LibraryLoadError.None);
    }

    public static LibraryItemsLoadResult Failure(LibraryLoadError error)
    {
        return new LibraryItemsLoadResult(false, null, null, error);
    }
}
