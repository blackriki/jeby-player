using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Search;

public interface ISearchService
{
    Task<SearchLoadResult> SearchAsync(
        AuthSession session,
        string keyword,
        CancellationToken cancellationToken);

    Task<SearchLoadResult> LoadFavoritesAsync(
        AuthSession session,
        CancellationToken cancellationToken);
}
