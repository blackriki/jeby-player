namespace EmbyPlayer.Core.Library;

public enum LibraryLoadError
{
    None,
    Unauthorized,
    Forbidden,
    ServerTimeout,
    ServerUnreachable,
    InvalidResponse,
    ServerError,
    Cancelled
}
