using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Playback;

public interface IPlaybackPreviewService
{
    Task<PlaybackPreviewResult> GetPreviewAsync(
        AuthSession session, string itemId, TimeSpan position, CancellationToken cancellationToken);
}

public enum PlaybackPreviewError
{
    None, Unavailable, Unauthorized, Forbidden, ServerError, ServerUnreachable,
    ServerTimeout, InvalidResponse, Cancelled
}

/// <summary>No image is a time-only preview; preview failures never change playback.</summary>
public sealed record PlaybackPreviewResult(
    byte[]? ImageBytes, TimeSpan? ImagePosition, PlaybackPreviewError Error)
{
    public static PlaybackPreviewResult Failure(PlaybackPreviewError error) => new(null, null, error);
}
