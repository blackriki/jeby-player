using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Player;

public interface ILocalPlaybackPreviewService : IAsyncDisposable
{
    PlaybackPreviewResult? TryGetCachedPreview(long playbackInstanceId, PlaybackInfo info, TimeSpan position) => null;

    void Prefetch(long playbackInstanceId, PlaybackInfo info, TimeSpan position, CancellationToken playbackLifetimeToken) { }

    Task<PlaybackPreviewResult> GetPreviewAsync(long playbackInstanceId, PlaybackInfo info,
        TimeSpan position, CancellationToken cancellationToken);

    /// <summary>Only release the specified instance; null releases any current preview.</summary>
    Task ReleaseAsync(long? playbackInstanceId = null);
}
