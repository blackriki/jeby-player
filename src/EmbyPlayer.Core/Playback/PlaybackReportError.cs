namespace EmbyPlayer.Core.Playback;

public enum PlaybackReportError
{
    None,
    Unauthorized,
    Forbidden,
    PathMismatch,
    ServerUnreachable,
    ServerTimeout,
    ServerError,
    Cancelled
}
