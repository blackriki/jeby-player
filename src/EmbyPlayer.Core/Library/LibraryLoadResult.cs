namespace EmbyPlayer.Core.Library;

public sealed class LibraryLoadResult
{
    private LibraryLoadResult(
        bool isSuccess,
        IReadOnlyList<LibraryItem>? libraries,
        LibraryLoadError error)
    {
        IsSuccess = isSuccess;
        Libraries = libraries ?? Array.Empty<LibraryItem>();
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<LibraryItem> Libraries { get; }

    public LibraryLoadError Error { get; }

    public static LibraryLoadResult Success(IReadOnlyList<LibraryItem> libraries)
    {
        return new LibraryLoadResult(true, libraries, LibraryLoadError.None);
    }

    public static LibraryLoadResult Failure(LibraryLoadError error)
    {
        return new LibraryLoadResult(false, null, error);
    }
}
