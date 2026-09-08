namespace EmbyPlayer.Core.Details;

public enum MediaDetailLoadError
{
    None,
    Unauthorized,
    Forbidden,
    NotFound,
    ServerTimeout,
    ServerUnreachable,
    InvalidResponse,
    ServerError,
    Cancelled
}
