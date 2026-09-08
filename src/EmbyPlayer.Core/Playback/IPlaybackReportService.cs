using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Playback;

public interface IPlaybackReportService
{
    Task<PlaybackReportResult> ReportPlayingAsync(
        AuthSession session,
        PlaybackReportStartRequest request,
        CancellationToken cancellationToken);

    Task<PlaybackReportResult> ReportProgressAsync(
        AuthSession session,
        PlaybackReportProgressRequest request,
        CancellationToken cancellationToken);

    Task<PlaybackReportResult> ReportStoppedAsync(
        AuthSession session,
        PlaybackReportStoppedRequest request,
        CancellationToken cancellationToken);
}
