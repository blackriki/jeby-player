using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.UserData;

public interface IItemUserDataService
{
    Task<ItemUserDataResult> SetFavoriteAsync(
        AuthSession session,
        string itemId,
        bool isFavorite,
        CancellationToken cancellationToken);

    Task<ItemUserDataResult> SetPlayedAsync(
        AuthSession session,
        string itemId,
        bool isPlayed,
        CancellationToken cancellationToken);
}
