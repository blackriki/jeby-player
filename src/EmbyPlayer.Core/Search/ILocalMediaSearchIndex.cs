using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Search;

public interface ILocalMediaSearchIndex
{
    Task PrepareAsync(AuthSession session, CancellationToken cancellationToken);

    Task RefreshAsync(AuthSession session, CancellationToken cancellationToken);

    Task<LocalMediaSearchQueryResult> SearchAsync(
        AuthSession session,
        string keyword,
        CancellationToken cancellationToken);
}
