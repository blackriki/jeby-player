using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Library;

public interface IMediaLibraryScanService
{
    Task<MediaLibraryScanResult> RequestScanAsync(
        AuthSession session,
        CancellationToken cancellationToken);
}
