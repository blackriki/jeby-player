using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.WatchLater;

public interface IWatchLaterStore
{
    Task<IReadOnlyList<WatchLaterItem>> LoadAsync(AuthSession session, CancellationToken cancellationToken);
    Task<bool> IsSavedAsync(AuthSession session, string itemId, CancellationToken cancellationToken);
    Task AddAsync(AuthSession session, WatchLaterItem item, CancellationToken cancellationToken);
    Task RemoveAsync(AuthSession session, string itemId, CancellationToken cancellationToken);
    Task ResetCorruptedAsync(AuthSession session, CancellationToken cancellationToken);
}
