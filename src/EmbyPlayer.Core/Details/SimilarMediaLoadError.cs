namespace EmbyPlayer.Core.Details;

public enum SimilarMediaLoadError
{
    None,
    Unauthorized,
    Forbidden,
    NotFound,
    ServerUnreachable,
    ServerTimeout,
    ServerError,
    InvalidResponse,
    Cancelled
}
