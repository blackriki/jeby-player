using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Emby;

public sealed class EmbyPlaybackPreviewService(HttpClient httpClient, IDeviceIdService deviceIdService) : IPlaybackPreviewService
{
    private readonly IEmbyApiClient apiClient = new EmbyApiClient(httpClient, deviceIdService);
    private readonly SemaphoreSlim gate = new(1, 1);
    private AuthSession? cachedSession;
    private string? cachedItemId;
    private BifPreviewArchive? cachedArchive;
    private PlaybackPreviewError cachedError;
    private DateTimeOffset cacheExpires;

    public async Task<PlaybackPreviewResult> GetPreviewAsync(
        AuthSession session, string itemId, TimeSpan position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(itemId)) return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(session, cachedSession) || cachedItemId != itemId || cacheExpires <= DateTimeOffset.UtcNow)
                {
                    // Retain only one item's archive, and never reuse it across authentication sessions.
                    cachedArchive = null;
                    cachedSession = null;
                    var result = await apiClient.GetPlaybackPreviewArchiveAsync(session, itemId, cancellationToken).ConfigureAwait(false);
                    if (result.Error == PlaybackPreviewError.Cancelled) return PlaybackPreviewResult.Failure(result.Error);
                    cachedSession = session;
                    cachedItemId = itemId;
                    cachedArchive = result.Archive;
                    cachedError = result.Error;
                    cacheExpires = DateTimeOffset.UtcNow.Add(result.Archive is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(30));
                }
                return cachedArchive?.GetFrame(position) ?? PlaybackPreviewResult.Failure(cachedError);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlaybackPreviewResult.Failure(PlaybackPreviewError.Cancelled);
        }
    }

}
