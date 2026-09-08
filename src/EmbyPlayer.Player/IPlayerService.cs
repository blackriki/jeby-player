using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Player;

public interface IPlayerService : IAsyncDisposable
{
    event EventHandler<PlayerStatusChangedEventArgs>? StatusChanged;

    event EventHandler<PlayerProgressChangedEventArgs>? ProgressChanged;

    Task<PlayerOperationResult> LoadAsync(
        PlayerLoadRequest request,
        CancellationToken cancellationToken);

    Task<PlayerOperationResult> PlayAsync(CancellationToken cancellationToken);

    Task<PlayerOperationResult> PauseAsync(CancellationToken cancellationToken);

    Task<PlayerOperationResult> SeekAsync(
        long playbackInstanceId,
        TimeSpan position,
        CancellationToken cancellationToken);

    Task<PlayerOperationResult> SetVolumeAsync(
        int volume,
        CancellationToken cancellationToken);

    Task<PlayerOperationResult> SetMuteAsync(
        bool isMuted,
        CancellationToken cancellationToken);

    Task<PlayerOperationResult> SelectAudioTrackAsync(
        long playbackInstanceId,
        int mpvTrackId,
        CancellationToken cancellationToken);

    Task<PlayerOperationResult> SelectSubtitleTrackAsync(
        long playbackInstanceId,
        int mpvTrackId,
        CancellationToken cancellationToken);

    Task<PlayerOperationResult> SelectExternalSubtitleAsync(
        long playbackInstanceId,
        int mediaStreamIndex,
        CancellationToken cancellationToken);

    Task<PlayerOperationResult> DisableSubtitleAsync(
        long playbackInstanceId,
        CancellationToken cancellationToken);

    Task<LocalSubtitleImportResult> ImportLocalSubtitleAsync(
        long playbackInstanceId, string filePath, CancellationToken cancellationToken)
        => Task.FromResult(new LocalSubtitleImportResult(false, Array.Empty<PlayerTrackInfo>()));

    Task<PlayerOperationResult> StopAsync(CancellationToken cancellationToken);

    Task<PlayerOperationResult> SetPlaybackSpeedAsync(
        long playbackInstanceId,
        double speed,
        CancellationToken cancellationToken)
        => Task.FromResult(PlayerOperationResult.Failure(PlayerError.PlaybackSpeedFailed));

    Task<PlayerOperationResult> SetSubtitleAdjustmentAsync(
        long playbackInstanceId,
        SubtitleAdjustmentKind kind,
        double value,
        CancellationToken cancellationToken)
        => Task.FromResult(PlayerOperationResult.Failure(PlayerError.SubtitleAdjustmentFailed));

    Task<PlayerTechnicalInfo?> GetTechnicalInfoAsync(
        long playbackInstanceId,
        CancellationToken cancellationToken)
        => Task.FromResult<PlayerTechnicalInfo?>(null);
}
