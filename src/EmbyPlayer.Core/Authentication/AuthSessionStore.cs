using EmbyPlayer.Core.Settings;

namespace EmbyPlayer.Core.Authentication;

public sealed class AuthSessionStore : IAuthSessionStore
{
    private readonly IAppSettingsService appSettingsService;
    private readonly IAuthSessionTokenStore tokenStore;

    public AuthSessionStore(
        IAppSettingsService appSettingsService,
        IAuthSessionTokenStore tokenStore)
    {
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        this.tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    public async Task SaveAsync(AuthSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var credentialTarget = AuthSessionCredentialTarget.Create(session.ServerBase, session.UserId);
        await tokenStore
            .SaveTokenAsync(credentialTarget, session.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var metadata = new AuthSessionMetadata(
                session.ServerBase,
                session.UserId,
                session.UserName,
                session.ServerId);

            await appSettingsService
                .SaveAuthSessionMetadataAsync(metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception metadataSaveException)
        {
            try
            {
                await tokenStore
                    .DeleteTokenAsync(credentialTarget, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception tokenDeleteException)
            {
                throw new AggregateException(
                    "Authentication metadata could not be saved and the secure-token rollback also failed.",
                    metadataSaveException,
                    tokenDeleteException);
            }

            throw;
        }
    }

    public async Task<AuthSession?> LoadAsync(CancellationToken cancellationToken)
    {
        var metadata = await appSettingsService
            .GetAuthSessionMetadataAsync(cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var credentialTarget = AuthSessionCredentialTarget.Create(metadata.ServerBase, metadata.UserId);
        var accessToken = await tokenStore
            .LoadTokenAsync(credentialTarget, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                await appSettingsService
                    .ClearAuthSessionMetadataAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new AuthSessionClearPartialFailureException(exception);
            }

            return null;
        }

        return new AuthSession(
            metadata.ServerBase,
            accessToken,
            metadata.UserId,
            metadata.UserName,
            metadata.ServerId);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        var metadata = await appSettingsService
            .GetAuthSessionMetadataAsync(cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            return;
        }

        var credentialTarget = AuthSessionCredentialTarget.Create(metadata.ServerBase, metadata.UserId);
        await tokenStore
            .DeleteTokenAsync(credentialTarget, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await appSettingsService
                .ClearAuthSessionMetadataAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new AuthSessionClearPartialFailureException(exception);
        }
    }
}

public sealed class AuthSessionClearPartialFailureException : Exception
{
    public AuthSessionClearPartialFailureException(Exception innerException)
        : base("The secure authentication token was cleared, but session metadata could not be cleared.", innerException)
    {
    }
}
