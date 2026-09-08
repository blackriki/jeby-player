namespace EmbyPlayer.Core.Search;

public enum SearchLoadError
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
