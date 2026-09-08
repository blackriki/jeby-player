namespace EmbyPlayer.Player;

public sealed record LocalSubtitleImportResult(bool IsSuccess, IReadOnlyList<PlayerTrackInfo> Tracks);
