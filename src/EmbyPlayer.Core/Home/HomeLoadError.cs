namespace EmbyPlayer.Core.Home;

public enum HomeLoadError
{
    None,
    Unauthorized,
    Forbidden,
    ServerUnreachable,
    ServerTimeout,
    ServerError,
    InvalidResponse,
    Cancelled
}
