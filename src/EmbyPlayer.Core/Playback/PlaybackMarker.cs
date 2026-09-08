namespace EmbyPlayer.Core.Playback;

public sealed record PlaybackMarker(
    PlaybackMarkerType Type,
    long StartPositionTicks);
