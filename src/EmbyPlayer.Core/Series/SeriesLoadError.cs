namespace EmbyPlayer.Core.Series;

public enum SeriesLoadError
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
