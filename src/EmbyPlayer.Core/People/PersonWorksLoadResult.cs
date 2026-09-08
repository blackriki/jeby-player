using EmbyPlayer.Core.Library;

namespace EmbyPlayer.Core.People;

public sealed record PersonWorksLoadResult(
    IReadOnlyList<LibraryMediaItem> Items, int NextStartIndex, bool HasMore, PersonLoadError Error)
{
    public bool IsSuccess => Error == PersonLoadError.None;

    public static PersonWorksLoadResult Success(IReadOnlyList<LibraryMediaItem> items, int nextStartIndex, bool hasMore)
        => new(items, nextStartIndex, hasMore, PersonLoadError.None);

    public static PersonWorksLoadResult Failure(PersonLoadError error)
        => new(Array.Empty<LibraryMediaItem>(), 0, false, error);
}
