using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.UI.ViewModels;

internal static class ExpiredSessionRecovery
{
    public static async Task ClearAsync(
        IAuthSessionStore authSessionStore,
        ICurrentSessionService currentSessionService,
        string source,
        AuthSession? expectedSession = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await authSessionStore
                .ClearAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"Failed to clear expired {source} session from persistent storage: {exception}");
        }

        if (!cancellationToken.IsCancellationRequested
            && (expectedSession is null || ReferenceEquals(expectedSession, currentSessionService.CurrentSession)))
        {
            currentSessionService.ClearSession();
        }
    }
}
