namespace EmbyPlayer.Core.Authentication;

public sealed class CurrentSessionService : ICurrentSessionService
{
    public AuthSession? CurrentSession { get; private set; }

    public void SetSession(AuthSession session)
    {
        CurrentSession = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void ClearSession()
    {
        CurrentSession = null;
    }
}
