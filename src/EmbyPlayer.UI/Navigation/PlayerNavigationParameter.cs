using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.UI.Navigation;

public sealed record PlayerNavigationParameter(
    PlaybackInfo PlaybackInfo,
    DetailNavigationParameter BackToDetailParameter,
    string? LogoUrl = null,
    bool IsQueuedPlayback = false);
