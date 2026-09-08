using EmbyPlayer.Core.Settings;

namespace EmbyPlayer.Core.Authentication;

public sealed class AccountSessionService : IAccountSessionService
{
    private readonly IAppSettingsService appSettingsService;
    private readonly IAuthSessionStore authSessionStore;
    private readonly ICurrentSessionService currentSessionService;

    public AccountSessionService(
        IAuthSessionStore authSessionStore,
        ICurrentSessionService currentSessionService,
        IAppSettingsService appSettingsService)
    {
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        await ClearAuthenticationAndRuntimeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SwitchServerAsync(CancellationToken cancellationToken)
    {
        await ClearAuthenticationAndRuntimeAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await appSettingsService
                .ClearLastServerBaseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new SwitchServerPartialFailureException(exception);
        }
    }

    private async Task ClearAuthenticationAndRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await authSessionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AuthSessionClearPartialFailureException)
        {
            currentSessionService.ClearSession();
            throw;
        }

        currentSessionService.ClearSession();
    }
}

public sealed class SwitchServerPartialFailureException : Exception
{
    public SwitchServerPartialFailureException(Exception innerException)
        : base("The account session was cleared, but the saved server address could not be cleared.", innerException)
    {
    }
}
