namespace EmbyPlayer.Core.Playback;

public enum PlaybackLoadError
{
    None,
    Unauthorized,
    Forbidden,
    NotFound,
    NoPlayableMediaSource,
    ServerUnreachable,
    ServerTimeout,
    ServerError,
    InvalidResponse,
    Cancelled
}
