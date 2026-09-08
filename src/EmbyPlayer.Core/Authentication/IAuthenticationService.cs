namespace EmbyPlayer.Core.Authentication;

public interface IAuthenticationService
{
    Task<AuthenticationResult> AuthenticateAsync(
        string serverBase,
        string userName,
        string password,
        CancellationToken cancellationToken);
}
