namespace EmbyPlayer.Core.Library;

public enum LibrarySortField
{
    Title,
    DateAdded,
    Year
}

public enum LibrarySortDirection
{
    Ascending,
    Descending
}

public enum LibraryWatchedFilter
{
    All,
    Unwatched,
    Watched
}

public sealed record LibraryQuery(
    LibrarySortField SortField = LibrarySortField.Title,
    LibrarySortDirection SortDirection = LibrarySortDirection.Ascending,
    LibraryWatchedFilter WatchedFilter = LibraryWatchedFilter.All,
    bool FavoritesOnly = false);
