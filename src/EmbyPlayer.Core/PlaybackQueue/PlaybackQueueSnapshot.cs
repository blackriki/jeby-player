namespace EmbyPlayer.Core.PlaybackQueue;

public sealed record PlaybackQueueSnapshot(
    long SessionVersion,
    bool HasSession,
    PlaybackQueueItem? Current,
    IReadOnlyList<PlaybackQueueItem> Pending,
    bool AutoPlayEnabled);
