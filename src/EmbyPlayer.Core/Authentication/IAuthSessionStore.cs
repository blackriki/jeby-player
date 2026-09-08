namespace EmbyPlayer.Core.Authentication;

public interface IAuthSessionStore
{
    Task SaveAsync(AuthSession session, CancellationToken cancellationToken);

    Task<AuthSession?> LoadAsync(CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
