namespace EmbyPlayer.Core.Home;

public sealed class HomeSectionItemsLoadResult
{
    private HomeSectionItemsLoadResult(
        bool isSuccess,
        IReadOnlyList<MediaCard> items,
        int nextStartIndex,
        bool hasMore,
        HomeLoadError error)
    {
        IsSuccess = isSuccess;
        Items = items;
        NextStartIndex = nextStartIndex;
        HasMore = hasMore;
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<MediaCard> Items { get; }

    public int NextStartIndex { get; }

    public bool HasMore { get; }

    public HomeLoadError Error { get; }

    public static HomeSectionItemsLoadResult Success(
        IReadOnlyList<MediaCard> items,
        int nextStartIndex,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new HomeSectionItemsLoadResult(true, items, nextStartIndex, hasMore, HomeLoadError.None);
    }

    public static HomeSectionItemsLoadResult Failure(HomeLoadError error)
    {
        return new HomeSectionItemsLoadResult(false, Array.Empty<MediaCard>(), 0, false, error);
    }
}
