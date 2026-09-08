namespace EmbyPlayer.Core.Home;

public sealed class HomeData
{
    public HomeData(
        string userName,
        IReadOnlyList<MediaCard> continueWatching,
        IReadOnlyList<MediaCard> recentlyAdded,
        IReadOnlyList<MediaLibrary> libraries,
        IReadOnlyList<HomeMediaSection>? mediaSections = null)
    {
        UserName = userName ?? string.Empty;
        ContinueWatching = continueWatching ?? Array.Empty<MediaCard>();
        RecentlyAdded = recentlyAdded ?? Array.Empty<MediaCard>();
        Libraries = libraries ?? Array.Empty<MediaLibrary>();
        MediaSections = mediaSections ?? Array.Empty<HomeMediaSection>();
    }

    public string UserName { get; }

    public IReadOnlyList<MediaCard> ContinueWatching { get; }

    public IReadOnlyList<MediaCard> RecentlyAdded { get; }

    public IReadOnlyList<MediaLibrary> Libraries { get; }

    public IReadOnlyList<HomeMediaSection> MediaSections { get; }
}

public sealed class HomeMediaSection
{
    private HomeMediaSection(string id, string title, bool isSuccess, IReadOnlyList<MediaCard> items)
    {
        Id = id;
        Title = title;
        IsSuccess = isSuccess;
        Items = items;
    }

    public string Id { get; }

    public string Title { get; }

    public bool IsSuccess { get; }

    public IReadOnlyList<MediaCard> Items { get; }

    public static HomeMediaSection Success(string id, string title, IReadOnlyList<MediaCard> items)
    {
        return new HomeMediaSection(id, title, true, items ?? Array.Empty<MediaCard>());
    }

    public static HomeMediaSection Failure(string id, string title)
    {
        return new HomeMediaSection(id, title, false, Array.Empty<MediaCard>());
    }
}
