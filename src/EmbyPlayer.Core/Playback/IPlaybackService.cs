using EmbyPlayer.Core.Authentication;

namespace EmbyPlayer.Core.Playback;

public interface IPlaybackService
{
    /// <summary>
    /// Negotiates a new playback path without changing or stopping any active player.
    /// Quality switches should pass the current source id, position and Emby stream indexes;
    /// a null stream index uses the server default and subtitle index -1 disables subtitles.
    /// Callers retain their existing playback when negotiation fails and replace it only after success.
    /// </summary>
    Task<PlaybackLoadResult> PreparePlaybackAsync(
        AuthSession session,
        PlaybackStartRequest request,
        CancellationToken cancellationToken);
}
