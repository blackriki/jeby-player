namespace EmbyPlayer.Core.Images;

public enum ImageLoadError
{
    None,
    Unauthorized,
    NotFound,
    ServerUnreachable,
    ServerTimeout,
    ServerError,
    InvalidResponse,
    Cancelled
}
