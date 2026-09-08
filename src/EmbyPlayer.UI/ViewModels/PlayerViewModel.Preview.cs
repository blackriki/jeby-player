using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private readonly IPlaybackPreviewService? playbackPreviewService;
    private readonly ILocalPlaybackPreviewService? localPlaybackPreviewService;
    private PlaybackInfo? localPreviewSource;
    private EmbyPlayer.Core.Authentication.AuthSession? localPreviewSession;
    private TimeSpan? previewHoverPosition;

    public PlaybackPreviewResult? TryGetSeekPreview(TimeSpan position)
    {
        previewHoverPosition = position;
        var info = PlaybackInfo;
        if (!CanSeek || info is null || !ReferenceEquals(info, localPreviewSource)
            || !ReferenceEquals(localPreviewSession, currentSessionService?.CurrentSession)
            || localPlaybackPreviewService is null || playbackLifecycleCancellation is null) return null;
        localPlaybackPreviewService.Prefetch(currentPlaybackInstanceId, info, position, playbackLifecycleCancellation.Token);
        return localPlaybackPreviewService.TryGetCachedPreview(currentPlaybackInstanceId, info, position);
    }

    public async Task<PlaybackPreviewResult> GetSeekPreviewAsync(TimeSpan position, CancellationToken cancellationToken,
        CancellationToken localDecodeCancellation = default)
    {
        var session = currentSessionService?.CurrentSession;
        var instance = currentPlaybackInstanceId;
        var info = PlaybackInfo;
        if (session is null || info is null || !CanSeek)
            return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            playbackLifecycleCancellation?.Token ?? CancellationToken.None);
        var result = playbackPreviewService is null
            ? PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable)
            : await playbackPreviewService.GetPreviewAsync(session, info.ItemId, position, linked.Token).ConfigureAwait(true);
        if (!CanAcceptPlayerEvent(instance) || !ReferenceEquals(session, currentSessionService?.CurrentSession))
            return PlaybackPreviewResult.Failure(PlaybackPreviewError.Cancelled);
        if (result.Error == PlaybackPreviewError.Unauthorized)
            await HandleExpiredSessionAsync(instance).ConfigureAwait(true);
        if (result.Error == PlaybackPreviewError.Unavailable && localPlaybackPreviewService is not null
            && !info.RequiresTranscoding && !linked.IsCancellationRequested)
        {
            localPreviewSource = info;
            localPreviewSession = session;
            localPlaybackPreviewService.Prefetch(instance, info, previewHoverPosition ?? position,
                playbackLifecycleCancellation?.Token ?? linked.Token);
            using var decodeCancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, localDecodeCancellation);
            result = await localPlaybackPreviewService.GetPreviewAsync(instance, info, position, decodeCancellation.Token).ConfigureAwait(true);
            if (decodeCancellation.IsCancellationRequested)
                return PlaybackPreviewResult.Failure(PlaybackPreviewError.Cancelled);
        }
        if (linked.IsCancellationRequested || !CanAcceptPlayerEvent(instance)
            || !ReferenceEquals(session, currentSessionService?.CurrentSession))
            return PlaybackPreviewResult.Failure(PlaybackPreviewError.Cancelled);
        return result;
    }

    private async Task ReleaseSeekPreviewAsync(long instance)
    {
        localPreviewSource = null;
        localPreviewSession = null;
        previewHoverPosition = null;
        if (localPlaybackPreviewService is null) return;
        try { await localPlaybackPreviewService.ReleaseAsync(instance).ConfigureAwait(true); }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Seek preview cleanup failed: {exception.GetType().Name}");
        }
    }
}
