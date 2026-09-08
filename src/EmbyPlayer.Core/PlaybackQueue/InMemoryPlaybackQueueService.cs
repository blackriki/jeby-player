using EmbyPlayer.Core.Servers;

namespace EmbyPlayer.Core.PlaybackQueue;

public sealed class InMemoryPlaybackQueueService : IPlaybackQueueService
{
    private readonly object gate = new();
    private string? serverBase;
    private string? userId;
    private PlaybackQueueSnapshot snapshot = new(0, false, null, Array.Empty<PlaybackQueueItem>(), true);

    public PlaybackQueueSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public event EventHandler? Changed;

    public void SetSession(string? serverBase, string? userId)
    {
        var normalized = ServerUrlNormalizer.Normalize(serverBase);
        var nextServer = normalized.IsSuccess && !string.IsNullOrWhiteSpace(userId)
            ? normalized.Server!.ServerBase : null;
        var nextUser = nextServer is null ? null : userId;
        lock (gate)
        {
            if (this.serverBase == nextServer && this.userId == nextUser) return;
            this.serverBase = nextServer;
            this.userId = nextUser;
            snapshot = new(snapshot.SessionVersion + 1, nextServer is not null, null,
                Array.Empty<PlaybackQueueItem>(), true);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Add(PlaybackQueueItem item)
    {
        var normalized = NormalizeItem(item);
        if (normalized is null) return false;
        lock (gate)
        {
            if (!snapshot.HasSession || snapshot.Current?.ItemId == normalized.ItemId
                || snapshot.Pending.Any(existing => existing.ItemId == normalized.ItemId)) return false;
            snapshot = snapshot with { Pending = Freeze(snapshot.Pending.Append(normalized)) };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Remove(string itemId)
    {
        lock (gate)
        {
            if (!snapshot.Pending.Any(item => item.ItemId == itemId)) return false;
            snapshot = snapshot with { Pending = Freeze(snapshot.Pending.Where(item => item.ItemId != itemId)) };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Move(string itemId, int destinationIndex)
    {
        lock (gate)
        {
            var items = snapshot.Pending.ToList();
            var oldIndex = items.FindIndex(item => item.ItemId == itemId);
            if (oldIndex < 0 || destinationIndex < 0 || destinationIndex >= items.Count || oldIndex == destinationIndex) return false;
            var item = items[oldIndex];
            items.RemoveAt(oldIndex);
            items.Insert(destinationIndex, item);
            snapshot = snapshot with { Pending = Freeze(items) };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool SetCurrent(PlaybackQueueItem item, long expectedSessionVersion)
    {
        var normalized = NormalizeItem(item);
        if (normalized is null) return false;
        lock (gate)
        {
            if (!snapshot.HasSession || snapshot.SessionVersion != expectedSessionVersion) return false;
            if (snapshot.Current?.ItemId == normalized.ItemId) return true;
            // Keep the queue's artwork/title when playback metadata did not carry them forward.
            var queued = snapshot.Pending.FirstOrDefault(existing => existing.ItemId == normalized.ItemId);
            snapshot = snapshot with
            {
                Current = queued ?? normalized,
                Pending = Freeze(snapshot.Pending.Where(existing => existing.ItemId != normalized.ItemId))
            };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool ClearCurrent(long expectedSessionVersion)
    {
        lock (gate)
        {
            if (snapshot.SessionVersion != expectedSessionVersion || snapshot.Current is null) return false;
            snapshot = snapshot with { Current = null };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearPending()
    {
        lock (gate)
        {
            if (snapshot.Pending.Count == 0) return;
            snapshot = snapshot with { Pending = Array.Empty<PlaybackQueueItem>() };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutoPlayEnabled(bool enabled)
    {
        lock (gate)
        {
            if (!snapshot.HasSession || snapshot.AutoPlayEnabled == enabled) return;
            snapshot = snapshot with { AutoPlayEnabled = enabled };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGetNext(out PlaybackQueueItem? item)
    {
        lock (gate)
        {
            item = snapshot.Pending.FirstOrDefault();
            return item is not null;
        }
    }

    private static IReadOnlyList<PlaybackQueueItem> Freeze(IEnumerable<PlaybackQueueItem> items)
        => Array.AsReadOnly(items.ToArray());

    private static PlaybackQueueItem? NormalizeItem(PlaybackQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.ItemId)) return null;
        var type = item.MediaType?.Trim().ToLowerInvariant() switch
        {
            "movie" => "Movie",
            "episode" => "Episode",
            _ => null
        };
        return type is null ? null : item with
        {
            ItemId = item.ItemId.Trim(),
            Title = string.IsNullOrWhiteSpace(item.Title) ? "未命名媒体" : item.Title.Trim(),
            MediaType = type
        };
    }
}
