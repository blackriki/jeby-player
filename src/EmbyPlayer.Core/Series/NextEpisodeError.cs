namespace EmbyPlayer.Core.Series;

public enum NextEpisodeError
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
