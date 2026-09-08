namespace EmbyPlayer.Core.PlaybackQueue;

public interface IPlaybackQueueService
{
    PlaybackQueueSnapshot Snapshot { get; }
    event EventHandler? Changed;

    /// <summary>Changing server/user or signing out discards this in-memory queue.</summary>
    void SetSession(string? serverBase, string? userId);
    bool Add(PlaybackQueueItem item);
    bool Remove(string itemId);
    /// <summary>Moves a pending item to a zero-based index in the resulting pending list.</summary>
    bool Move(string itemId, int destinationIndex);
    /// <summary>Call only after playback succeeds. A stale session cannot consume a new queue.</summary>
    bool SetCurrent(PlaybackQueueItem item, long expectedSessionVersion);
    bool ClearCurrent(long expectedSessionVersion);
    void ClearPending();
    void SetAutoPlayEnabled(bool enabled);
    /// <summary>Peeks without consuming. The caller checks AutoPlayEnabled for automatic playback.</summary>
    bool TryGetNext(out PlaybackQueueItem? item);
}
