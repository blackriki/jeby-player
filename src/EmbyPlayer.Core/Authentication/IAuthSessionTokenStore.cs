namespace EmbyPlayer.Core.Authentication;

public interface IAuthSessionTokenStore
{
    Task SaveTokenAsync(string credentialTarget, string accessToken, CancellationToken cancellationToken);

    Task<string?> LoadTokenAsync(string credentialTarget, CancellationToken cancellationToken);

    Task DeleteTokenAsync(string credentialTarget, CancellationToken cancellationToken);
}
