namespace EmbyPlayer.Core.Authentication;

public interface ICurrentSessionService
{
    AuthSession? CurrentSession { get; }

    void SetSession(AuthSession session);

    void ClearSession();
}
