namespace EmbyPlayer.Core.Authentication;

public interface IAccountSessionService
{
    Task LogoutAsync(CancellationToken cancellationToken);

    Task SwitchServerAsync(CancellationToken cancellationToken);
}
