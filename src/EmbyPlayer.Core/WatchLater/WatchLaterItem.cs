namespace EmbyPlayer.Core.WatchLater;

public sealed record WatchLaterItem(
    string ItemId,
    string Title,
    string? ImageUrl,
    string MediaType,
    int? ProductionYear);
