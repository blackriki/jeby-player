namespace EmbyPlayer.Core.PlaybackQueue;

public sealed record PlaybackQueueItem(
    string ItemId,
    string Title,
    string MediaType,
    int? ProductionYear = null,
    string? ImageUrl = null);
