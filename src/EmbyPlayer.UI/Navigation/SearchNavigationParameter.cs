namespace EmbyPlayer.UI.Navigation;

public sealed record SearchNavigationParameter(string? Keyword, bool FavoritesOnly)
{
    public static SearchNavigationParameter Search(string? keyword)
    {
        return new SearchNavigationParameter(keyword, false);
    }

    public static SearchNavigationParameter Favorites()
    {
        return new SearchNavigationParameter(null, true);
    }
}
