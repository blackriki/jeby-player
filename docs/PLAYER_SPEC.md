# Jeby Player 1.0 Player Specification

## 1. Purpose

This document defines the expected behavior of the MPV-based player module.

The player is the most important part of the app. 1.0 should prioritize stable playback and correct state reporting over fancy effects.

### Playback speed and held-frame gesture

- The speed menu offers 0.5×, 0.75×, 1×, 1.25×, 1.5×, 1.75×, 2× and 3×; MPV changes speed through IPlayerService with pitch correction enabled. Reject non-finite values and values outside 0.25–4.
- Speed applies to the current media. A new item starts at 1×; reloading the same item for a quality change preserves the selected speed, never a temporary held-frame override.
- Holding the left mouse button on the video surface for 450 ms temporarily uses 2× while playing. Release restores the selected speed and keeps playback running. A hold on a paused frame does not start playback.
- A short click toggles pause on release; double-click retains fullscreen behavior. Buttons, sliders, window dragging/resizing and popup interaction must not start the gesture. Moving more than 12 DIP cancels the gesture without a click.
- Capture loss, app deactivation, navigation, loading and pause cancel the hold. Serialize speed changes so release before an in-flight 2× request completes still restores the selected speed. Old-instance completions cannot alter the new item's state.
- Failed speed changes retain the actual speed and offer visible retry, including restoration failures; do not report success or forget the user's original selected speed.

### Track preferences across episodes

- Existing preferred audio/subtitle languages continue to apply. A saved DefaultSubtitlesEnabled preference defaults to true for older settings; false selects subtitle Off on new media unless an explicit same-series manual selection applies.
- Successful manual track selections, including explicitly selecting the already-active track or Off, are remembered within the current authenticated session and identified series. Match language, title, codec and external-subtitle status rather than carrying stream indexes across episodes.
- Missing remembered tracks fall back to global preferences. Switching series, playing a movie, or changing authenticated session clears remembered selections. Quality reloads retain their explicit track selection ahead of these defaults.
- Playback metadata carries the server-provided SeriesId for episodes. Missing optional metadata must not prevent playback or fabricate a series identity.

## 2. Player Architecture

### Required Layers

```text
Player Page / Player ViewModel
↓
IPlayerService
↓
MpvPlayerService
↓
MPV process / libmpv integration
```

UI must not call MPV directly.

### Core Interface

```csharp
public interface IPlayerService : IAsyncDisposable
{
    PlayerState State { get; }
    TimeSpan Position { get; }
    TimeSpan? Duration { get; }
    int Volume { get; }
    bool IsMuted { get; }

    event EventHandler<PlayerStateChangedEventArgs> StateChanged;
    event EventHandler<PlayerPositionChangedEventArgs> PositionChanged;
    event EventHandler<PlayerTrackChangedEventArgs> TrackChanged;
    event EventHandler<PlayerErrorEventArgs> ErrorOccurred;

    Task LoadAsync(PlayerMediaSource source, CancellationToken cancellationToken);
    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
    Task SeekRelativeAsync(TimeSpan offset, CancellationToken cancellationToken);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken);
    Task SelectAudioTrackAsync(PlayerTrack track, CancellationToken cancellationToken);
    Task SelectSubtitleTrackAsync(PlayerTrack? track, CancellationToken cancellationToken);
    Task SetFullscreenAsync(bool fullscreen, CancellationToken cancellationToken);
}
```

## 3. Player States

```text
Idle
Loading
Ready
Playing
Paused
Buffering
Seeking
Stopped
Completed
Error
Disposed
```

### State Rules

- `Idle`: no media loaded.
- `Loading`: playback info/stream is being prepared.
- `Ready`: media loaded, not yet playing.
- `Playing`: media actively playing.
- `Paused`: playback paused.
- `Buffering`: playback waiting for data.
- `Seeking`: seek command in progress.
- `Stopped`: user stopped/exited playback.
- `Completed`: playback reached completion.
- `Error`: unrecoverable playback error for current media.
- `Disposed`: player resources released.

## 4. Player Media Source

```csharp
public sealed class PlayerMediaSource
{
    public string ItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public Uri StreamUri { get; init; } = default!;
    public string? MediaSourceId { get; init; }
    public string? PlaySessionId { get; init; }
    public TimeSpan? ResumePosition { get; init; }
    public IReadOnlyList<PlayerTrack> AudioTracks { get; init; } = Array.Empty<PlayerTrack>();
    public IReadOnlyList<PlayerTrack> SubtitleTracks { get; init; } = Array.Empty<PlayerTrack>();
    public TimeSpan? Duration { get; init; }
}
```

## 5. Startup Playback Flow

```text
User clicks Play
↓
ViewModel calls EmbyApiClient.GetPlaybackInfoAsync
↓
ViewModel maps PlaybackInfo to PlayerMediaSource
↓
PlayerService.LoadAsync
↓
PlayerService.PlayAsync
↓
PlaybackProgressService.ReportStartedAsync
↓
Start progress timer
```

## 6. Exit Playback Flow

```text
User presses Esc / Back
↓
If fullscreen: exit fullscreen only
Else: confirm only if needed, then stop playback
↓
Report stopped position
↓
Dispose or unload player resources
↓
Return to previous page
```

Do not lose the stopped position when the user exits quickly.
Capture one final trusted position/runtime snapshot before awaiting network or player shutdown. Use that same immutable snapshot and synchronization result for the final stopped report and for the detail-page return state so a movie or episode card can update immediately without waiting for Emby user data to refresh. If completion already started the stopped report, Back awaits and reuses that first outcome; later player events cannot replace it.
For one playback instance, Playing, Progress, and Stopped reports are ordered. Once exit marks the instance as closing, queued Progress reports are dropped, an in-flight report is allowed to finish, and one final Stopped report is sent last.
The returned local snapshot must protect the current item from a stale detail or episode response. A recoverable sync failure keeps the local resume state and shows a non-blocking warning; an unauthorized response still clears the session and returns to Login instead of Detail.
Only an explicit player-return or retry navigation parameter retains the local snapshot. Reopening the same item without that state clears the pending snapshot and warning, and an older load generation cannot restore them.

## 7. Playback Controls

### Play / Pause

- Space toggles play/pause.
- Play button toggles icon based on state.
- Pause should immediately update UI even if progress report is still pending.

### Seek

Default seek steps:

- Left: -10 seconds.
- Right: +10 seconds.

Rules:

- Clamp seek position to 0 and duration.
- Do not seek before media is ready.
- Do not spam seek events while dragging progress bar.
- Pressing an enabled progress-track position immediately previews that position and captures the pointer so the same press can continue as a smooth drag; commit one seek on release.
- Direct thumb dragging keeps native Slider behavior; canceled track or thumb drags restore the reported playback position without seeking.
- Tap the configured backward/forward shortcut to move one configured step on release. Holding beyond 400 ms accelerates the target preview; send one seek on release, independent of operating-system key repeat. Preserve the prior paused/playing state.
- Show a compact direction/target-time hint during the key press. Focus loss, modifier changes, opening a player menu, fullscreen/mini changes, and leaving or replacing playback cancel the preview without a seek. Text fields and focused popup controls retain their keyboard handling.

### Volume

- Range: 0 to 100 by default.
- Up: +5.
- Down: -5.
- M toggles mute.
- Pressing the volume track updates the local preview immediately, captures the pointer, and continues smoothly until release. The volume popup does not take competing pointer capture, and pointer input anywhere inside its presented content remains internal even though Popup events tunnel through the logical overlay parent. Switching directly from volume to seek closes it without consuming the seek press. Owner/overlay activation changes keep it open, while whole-application deactivation closes it. Release commits once through the existing player and preference pipeline; capture loss restores the pre-drag value without committing.
- Direct thumb dragging retains the same preview-and-release behavior. Volume zero and explicit mute both use the muted icon without conflating the underlying mute state.
- Setting volume above 0 while muted may unmute if UX chooses; document final behavior.

### Fullscreen

- F toggles fullscreen.
- Double-click video toggles fullscreen, P1.
- Esc exits fullscreen first.
- If not fullscreen, Esc exits player.
- Restored, windowed maximized, and explicit fullscreen are separate states.
- Windowed maximized playback uses the current monitor work area so a visible taskbar never covers the bottom controls.
- Explicit fullscreen uses the complete current monitor, stays above the taskbar, and restores the exact prior window state on exit.
- Entering Player while already maximized, or returning to browsing, must preserve `WindowState.Maximized` and refresh native maximize bounds without a Normal-to-Maximized transition.
- Work-area and monitor rectangles are native physical pixels; per-monitor DPI conversion is only required when assigning WPF DIP geometry directly.

### Configurable Keyboard Controls

Settings stores player bindings with the general playback preferences. Users can record a key with optional Ctrl, Shift, and/or Alt, clear an action, or restore the complete default set. Duplicate gestures show the conflicting action and keep the previous binding. Reserved navigation/system gestures, including Esc, Tab, Windows keys, F10, Alt+F4, Alt+Space, Alt+Left/Right, and Ctrl+Alt+Delete, cannot be assigned. Missing legacy bindings use defaults; invalid or conflicting saved bindings fall back to the default set.

| Action | Default |
|---|---|
| Play / pause | Space |
| Seek backward / forward | Left / Right |
| Volume up / down | Up / Down |
| Mute | M |
| Fullscreen | F |
| Next subtitle, including Off | J |
| Subtitle earlier / later by 0.1 seconds | Ctrl+Left / Ctrl+Right |
| Next audio track | A |

Resolve the exact key/modifier combination; changing a binding removes its previous player action. Help text and fullscreen tooltip use the effective bindings. Existing loading/seek/track-transition guards still apply, and actions route through the current player service rather than a second MPV input pipeline.

Do not intercept player shortcuts while a text/password editor has focus, including an editable ComboBox's text input, or while keyboard focus is inside a player popup. Keep Esc reserved: close the open popup first, then leave fullscreen, then return from playback. Recording in Settings consumes keys only while a recording row is active; Esc or leaving that row cancels recording. Shortcut edits use Settings Save/discard behavior.

## 8. Control Overlay

### Mini window and window pinning

The player header offers explicit mini mode (480×270 DIP minimum) and window pinning. Mini entry saves the current normal/maximized/fullscreen state, bounds, minimum size and pin state, then opens a compact pinned window. Restore returns to that saved state; leaving playback restores browsing geometry and the original pin state. Mini mode retains transport controls and moves secondary operations into More. Caption hit areas shrink with mini chrome. Pinning is disabled during explicit fullscreen.

### Playback recovery

Fatal playback errors offer one user-initiated reconnect attempt at the last trusted position. Capture pause, quality, volume/mute, speed, selected tracks and subtitle adjustments before failed-state cleanup; preserve that snapshot across failed retries. Reuse fresh authenticated playback negotiation and the existing replacement-instance lifecycle. Old events and cancelled reconnections must not overwrite a newer item. Do not retry indefinitely or silently skip the item. Server-side transcode failures remain recoverable errors, not a guarantee that the client can repair the server.

### Timeline preview

Hovering the seek bar shows the target time without seeking. Prefer authenticated Emby BIF thumbnails. When the server has no thumbnails, a separate local MPV decoder may generate a frame from the existing directly readable playback source and its required HTTP headers. Never negotiate another playback session or request server transcoding for previews; transcoded sources and unavailable frames retain a time-only preview. This uses local decoding resources and additional media transfer from the server.

While moving, immediately show the nearest cached frame and sample new foreground targets at an 80 ms interval without requiring the pointer to settle. Keep the displayed frame until a replacement is ready. The card shows only the pointer target time; cached images provide approximate visual guidance. Retain the initial archive request and avoid repeatedly aborting and reopening the decoder on every pointer movement. Ignore completed frames for obsolete targets.

After confirming that server thumbnails are unavailable, use one serialized local decoder to prioritize foreground requests, prefetch nearby frames, and progressively fill a sparse set across the current video. Bound the in-memory frame cache to 96 frames / 24 MiB and throttle background work. Idle decoder cleanup retains thumbnails for the current playback session. Leaving the bar cancels its foreground request and hides the card; the bounded background warm-up can continue. Navigation, source/account changes and application exit cancel prefetch and clear its cache. Main-player position, audio, subtitles and progress reporting must remain unaffected.

### Behavior

- Visible on player open.
- Visible on mouse movement.
- Visible while paused.
- Visible while menus are open.
- Hidden after 3 seconds of mouse inactivity while playing.
- Cursor hidden when overlay hidden.

### Do Not

- Do not hide controls while user is dragging progress bar.
- Do not hide controls while subtitle/audio menu is open.
- Do not permanently cover subtitle area with opaque bottom bar.

## 9. Subtitles

### Subtitle Track Model

```csharp
public sealed class PlayerTrack
{
    public int Index { get; init; }
    public string Type { get; init; } = string.Empty; // Audio or Subtitle
    public string? Language { get; init; }
    public string? DisplayTitle { get; init; }
    public string? Codec { get; init; }
    public string? Channels { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsExternal { get; init; }
}
```

### Rules

- Subtitle menu must include `关闭字幕`.
- External and internal subtitles should both be supported if MPV supports the stream URL/path.
- Default subtitle preference should be applied after media load.
- If selected subtitle fails, show non-blocking error and keep playback running.

Temporary adjustments use `IPlayerService` and the active playback instance: MPV `sub-delay` in seconds (−60 to +60, step 0.1), `sub-scale` (0.5–3, step 0.1), and `sub-pos` (0–100, step 5). Reset applies 0 seconds, scale 1, and position 100. Update displayed values only after successful MPV operations; failure is non-blocking, and stale instance/canceled operations cannot affect replacement playback. Preserve adjustments during a quality reload of the same item; reset them for a new item. Do not persist them as preferences. Bitmap and styled subtitles may ignore size/position settings; server-burned subtitles cannot be manipulated as a local subtitle track.

### User-imported local subtitles

The subtitle menu can import one local SRT, ASS, SSA or VTT file; dragging one supported file onto the player uses the same operation. Show a non-blocking loading/result message and preserve playback on failure. Load through the player service with MPV argument arrays, deduplicate a repeated filename, and display the basename with a local-source label. Local tracks have no Emby stream index and must never be reported as server tracks. Keep the selected local file during a quality reload or error retry of the same item; do not persist or apply it to another episode/movie. Imported subtitles reuse the existing temporary subtitle adjustments.

## 10. Audio Tracks

### Rules

- Audio menu must show all available audio tracks.
- Display language, codec, and channels when available.
- Default audio preference should be applied after media load.
- If audio switch fails, show non-blocking error.

## 11. Playback Quality

The approved per-playback selector has four choices:

| Choice | Resolution ceiling | Total bitrate ceiling |
|---|---|---|
| Original | No client quality cap | No client quality cap |
| 1080p | 1920 × 1080 | 8 Mbps |
| 720p | 1280 × 720 | 4 Mbps |
| 480p | 854 × 480 | 1.5 Mbps |

Rules:

- Non-original choices negotiate a fresh authenticated PlaybackInfo response with server transcoding; the caps are not guaranteed output dimensions or measured throughput. Server permissions, source format, and transcoding support determine availability. Original uses the existing source-selection policy and may still require transcoding for compatibility.
- Prepare before stopping existing playback. Preparation failure preserves it; replacement-load failure attempts fresh negotiation of the previous quality. A failed restoration offers return/retry without pretending that playback recovered.
- Preserve current position, paused state, volume/mute, fullscreen, selected audio/subtitle, subtitle adjustments, and the current item's auto-next cancellation state during reload. Ignore obsolete events and stop/report each playback instance once. Seeking before a capped stream's server start offset renegotiates its start position; seeking within that stream uses its relative timeline.
- Do not implement quality switching by guessing stream URLs.

### Playback Information

Read actual video width/height and codec from MPV (`video-params/w`, `video-params/h`, `video-format`) for the active instance, on opening or refreshing the information popup. Show server source metadata and negotiated DirectPlay/DirectStream/Transcode separately. Missing runtime fields remain unavailable; source bitrate is not a measurement of network throughput or transcoded output bitrate. Reading information must not change pause, position, tracks, or reporting cadence.

## 12. Progress Reporting

### Service

Use a dedicated service:

```csharp
public interface IPlaybackProgressReporter
{
    Task ReportStartedAsync(PlayerMediaSource source, PlayerSnapshot snapshot, CancellationToken cancellationToken);
    Task ReportProgressAsync(PlayerMediaSource source, PlayerSnapshot snapshot, CancellationToken cancellationToken);
    Task ReportStoppedAsync(PlayerMediaSource source, PlayerSnapshot snapshot, CancellationToken cancellationToken);
}
```

### Snapshot

```csharp
public sealed class PlayerSnapshot
{
    public TimeSpan Position { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool IsPaused { get; init; }
    public bool IsMuted { get; init; }
    public int Volume { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
}
```

### Rules

- Progress timer starts after playback begins.
- Default interval: 10 seconds.
- Report immediately on pause/resume.
- Report immediately on stop/exit.
- Do not block player UI on reporting.
- Debounce repeated state changes.
- If network is unavailable, log and continue playback.
- Only an explicit playback-report `401` expires the session and returns the user to login. Failure to remove the persisted credential is logged but must not prevent stopping local playback, clearing the runtime session, showing the friendly expiry message, or navigating to Login.
- Playback-report `403`, timeout, server-unreachable, and `5xx` failures are non-fatal: keep local playback and the authenticated session active.
- Caller cancellation is silent. Other recoverable reporting failures may show one non-blocking warning per playback item, not a warning every report interval.

## 13. Completion Rules

Separate the local watched threshold from the end of playback. Consider the current position watched when:

- position / duration >= 90%, or
- remaining time is less than 2 minutes for `Movie` or `Episode` items with a runtime greater than 2 minutes. This is the client's long-form category convention; other or unknown types and clips of 2 minutes or less use only the percentage rule.

Crossing this threshold sends one immediate progress update with the actual position. Keep periodic reports running until exit or EOF, so a failed threshold update can recover on a later tick. Do not stop playback or automatically switch episodes at the watched threshold. On player return, derive local watched state from the final position, even when paused or after seeking; seeking backward below the threshold restores local resume behavior.

Playback ends only when MPV reports a successful natural EOF. Stop, error, redirect, and late EOF after closing must not become a new completion. When playback ends:

- Report final progress and one Stopped request. When duration is known, use that end position even if the final MPV position poll was missing; otherwise retain the last known position. Back reuses the immutable stopped outcome.
- Mark local state complete. The existing Emby reports contain real position/runtime, not an explicit watched flag; server watched state depends on its playstate behavior and requires real-server verification.
- At natural EOF, apply the queue priority in section 14 before considering automatic next episode. The watched threshold never consumes a queued item.

## 14. Auto-Play Next Episode, P1

The existing episode flow is the fallback when the pending queue is empty. `AutoPlayNextEpisode` defaults to true. It can find the next episode in the current or next season; the current episode's next-episode card supports immediate playback, a final-ten-seconds countdown, and dismissal that cancels automatic next episode for that item. Automatic replacement occurs only at successful natural EOF. Do not infer an automatic next movie.

### Explicit Session Queue

- The queue stores current and pending Movie/Episode items in memory for one normalized server/user session. A new session queue defaults to automatic continuation on. Logout, account/server change, or app restart clears the queue; no stream URL or token is retained in its item model.
- At natural EOF, a nonempty pending queue takes priority over `AutoPlayNextEpisode`. If queue continuation is on, prepare its first item. If it is off, report the current stop and leave the pending list intact; do not fall through to the episode fallback. Only an empty pending list permits the existing episode setting to decide what happens next.
- Manual queue play and `下一待播` work independently of the queue continuation toggle. Reordering changes the candidate used by the next transition. Removing/clearing pending items does not stop or remove current playback.
- Prepare the candidate while the current media is intact, then stop/report the old instance and load the new one. Peeking is non-consuming; only native Ready commits the candidate as current and removes it from pending. Preparation failure preserves old playback; replacement-load failure retains the candidate and reports an error, with no automatic skip to a later item.
- Prevent overlapping queue, next-episode, and quality transitions. Lock queue play/edit actions while a candidate is being prepared or loaded, including transitions initiated by the player. Reject results from an obsolete session or playback lifecycle, and cancel preparation when its owning queue view is left/closed.
- Keep final progress, Stopped, Started, and disposal bound to their own playback instance. Late EOF, cancellation, and load failure cannot advance a new queue or emit a duplicate successful start. The queue popup stays on Player so merely inspecting the list does not dispose the native player.

## 15. Error Handling

### Error Types

- PlaybackInfo unavailable.
- No playable media source.
- Stream URL invalid.
- MPV process failed.
- Unsupported codec.
- Network interrupted.
- Subtitle load failed.
- Audio switch failed.

### UI Behavior

Fatal playback errors show player error state:

```text
播放失败
[Reason]
[重试] [返回]
```

Recoverable errors use toast:

- subtitle switch failed
- audio switch failed
- progress sync failed

## 16. MPV Integration Requirements

Implementation may use libmpv or MPV IPC, but must satisfy:

- can load a URL stream
- can embed or display in WPF window
- can receive play/pause/seek/volume commands
- can expose duration and position
- can list or select audio/subtitle tracks
- can report errors
- can be disposed cleanly

Do not couple the UI directly to a specific MPV wrapper.

## 17. Player Tests

Unit tests should cover:

- state transitions
- seek clamping
- volume clamping
- shortcut command mapping
- progress reporting throttle
- progress stopped report on exit
- serialized report ordering with Stopped last
- immediate detail/episode resume merge after player exit
- stale detail/episode responses cannot roll back the returned snapshot
- subtitle track selection command
- audio track selection command
- error state mapping

Integration/manual tests should cover:

- local MP4 playback
- MKV with multiple audio tracks
- MKV with embedded subtitles
- external subtitle if supported
- stream URL from Emby PlaybackInfo
- network interruption behavior

## 18. Player Do Not Rules

Do not:

- use WPF MediaElement as the final 1.0 playback core unless MPV path is explicitly cancelled
- report progress every frame
- block UI while waiting for progress report
- crash app if MPV crashes
- lose stopped position on quick exit
- hide controls while user is interacting
- assume every item has subtitles
- assume every item has duration
- assume every stream is direct-playable
