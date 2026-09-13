# Jeby Player 1.0 Test Plan

## 1. Purpose

This test plan exists to reduce Codex-generated bugs by making completion measurable.

Every feature task should add or update tests when reasonable, and every completed task should report which tests were run.

Automated and Manual checklists below describe verification targets, not a record that every scenario passed. The five-feature work on `codex/playback-and-library-enhancements` remains pending final integration acceptance; historical release totals must not be reused as evidence for it.

## 2. Required Verification Commands

Use project-specific commands if they exist. Otherwise default to:

```bash
dotnet restore
dotnet build
dotnet test
```

For UI-only changes where automated UI tests are not available, still run build and relevant unit tests.

## 3. Completion Reporting

Every task must report:

```text
Validation
- dotnet build: passed/failed/not run
- dotnet test: passed/failed/not run

Not validated
- ...

Known risks
- ...
```

Do not say something works if it was not tested.

## 4. Test Layers

### 4.1 Unit Tests

Use for:

- URL normalization
- token storage abstraction behavior
- API error mapping
- DTO-to-domain mapping
- pagination state
- search debounce logic
- player state machine
- seek and volume commands
- progress reporting throttle

### 4.2 Integration Tests with Fake HTTP

Use for:

- authentication success/failure
- token expired response
- library fetch
- empty library response
- item detail fetch
- playback info parsing
- progress report success/failure

Do not require a real Emby Server for CI tests.

### 4.3 Manual Tests

Use for:

- real server login
- real media browsing
- real playback
- subtitle/audio switching
- fullscreen behavior
- visual polish
- keyboard shortcuts

## 5. Test Data Requirements

Prepare or mock these cases:

1. User with movie library.
2. User with TV library.
3. Empty library.
4. Movie with poster and backdrop.
5. Movie missing poster.
6. Movie with resume progress.
7. Movie with no subtitles.
8. Movie with one subtitle.
9. Movie with multiple subtitles.
10. Movie with one audio track.
11. Movie with multiple audio tracks.
12. Episode with next episode available.
13. Token expired response.
14. Server offline.
15. PlaybackInfo returns no playable source.

## 6. Feature Test Checklists

## 6.1 Server Connection

### Automated

- Empty URL disables Connect.
- Invalid URL returns validation error.
- URL normalization trims trailing slash.
- Existing `/emby` suffix is not duplicated.
- Reachable server returns success.
- Timeout maps to timeout error.
- Connection failure maps to unreachable error.
- Successful connections persist at most five normalized recent addresses in newest-first order; reconnecting moves an existing address to the front.
- Legacy last-server settings become history; removing all history and restarting does not restore removed entries.
- Logout and switch-server retain history; switch-server clears the input until the user chooses or types an address.
- Selecting history makes no connection request. Removing an entry leaves the current input, saved server, and authentication unchanged.
- Local read/save/remove failures show recoverable errors and reset busy state. A failed removal retains the row.
- Late settings loads do not overwrite user input or erase a switch-server partial-cleanup warning; repeated loads share their in-flight work.

### Manual

- Enter `http://192.168.x.x:8096`.
- Enter `https://domain.example.com`.
- Enter invalid text.
- Turn off server and retry.
- Open the connection page with five recent servers, select and remove rows by mouse and keyboard, and confirm the layout scrolls in a small window.
- Restart after removing a recent server and confirm it stays removed; switch server and confirm history remains while the input is empty.
- Confirm last successful server is saved.

## 6.2 Login

### Automated

- Valid credentials save token through secure storage abstraction.
- Password is never saved.
- Invalid credentials show friendly error.
- 401 clears token.
- Logout clears token and user info.
- Logout clears secure authentication before the runtime session while preserving server, device ID, playback preferences, and search history.
- Switch server clears authentication, runtime session, and saved server in that order while preserving general local preferences.
- Login switch-server prevents duplicate submission, navigates after full cleanup, and keeps the page/input when authentication cleanup fails.
- Server Connection re-entry clears stale URL/error before loading the current saved value.
- Startup token expiry remains failure-safe when persistent cleanup throws: runtime state is cleared, Login opens with a friendly warning, and startup completes without an async-void exception.
- Secure-token deletion followed by metadata-clear failure is reported as a typed partial cleanup; AccountSession clears runtime state, preserves the server target, and Login, Settings, and AppShell route to a safe page with an accurate warning.
- If only saved-address cleanup fails after authentication and runtime cleanup, Login switch-server opens Server Connection with the retained editable address and a partial-cleanup warning.
- A late saved-address load cannot overwrite newer user input. Concurrent loads on one Server Connection page coalesce, including when user input occurs between calls; a later page re-entry after completion starts a fresh load.

### Manual

- Login with valid account.
- Login with wrong password.
- Restart app after login.
- Logout and relaunch.
- Switch server.
- From Login, switch server without a second confirmation; verify the connection page has no stale server address.

## 6.3 Home Page

### Automated

- Home view model loads sections.
- Section failure does not fail all sections.
- Empty continue-watching state is handled.
- Missing poster uses placeholder.

- Every Home `查看全部` button has a working command and section-specific accessible name, including the generated movie/series/animation/box-set headers. Exercise real WPF bindings and button activation.
- Full-section requests retain Home scope, latest grouping, authentication, fallback, image/episode metadata, and source pagination beyond 24 cards. Animation paging merges all classified libraries in date order without duplicates or skipped boundary records.
- Full-section state covers first load, append, refresh, empty, error, retry, and duplicate IDs. Pagination uses the next source index rather than displayed-card count.
- Leaving a section, switching its kind, or changing session cancels in-flight work and rejects late success or unauthorized responses. Returning from Detail retains the section and loaded pages; authentication navigation clears them.

### Manual

- Confirm Continue Watching row appears when data exists.
- Confirm Recently Added row appears.
- Confirm media library entry opens library page.
- Confirm cards open details.
- Confirm scroll does not freeze.
- Activate `查看全部` on Continue Watching, Recently Added, movies, TV, animation, and collections using mouse and keyboard. Confirm matching titles and content, more than 24 results when available, and consistent button alignment.
- Open a full-list movie or resumable episode, then return and confirm section/list state and episode context. Test refresh, load more, empty lists, retry, and return-to-Home.

## 6.4 Library Page

### Automated

- First page loads.
- Next page loads when requested.
- Duplicate items are avoided.
- Empty library state appears.
- Error state includes retry command.
- Library query maps all three sort fields and both directions to documented Emby parameters; all six watch/favorite combinations remain server filters, including omission for all.
- Query defaults remain name ascending/all/all; the same value is a no-op, changing any option resets pagination, and later pages carry identical options.
- Pending old query and pagination requests are canceled. Late success, recoverable failure, or 401 cannot replace new results/session; late completion cannot end a newer loading indicator.
- Query failures clear prior-query cards and retry with the current options. Same-query refresh failures retain existing cards. Filtered empty results explain the active conditions.
- Repeated item IDs are deduplicated, and reaching the server total or an empty page ends pagination.
- Leaving Library cancels list/item loading. Same-session return retains query options; account/server/session changes clear old cards and restore defaults.
- An expired-session cleanup that finishes after a runtime session replacement must not clear that replacement or navigate it to Login. This check does not assert atomic protection against a credential store that ignores cancellation.

### Manual

- Open movie library.
- Scroll through at least 100 items if available.
- Open TV library.
- Open series.
- Switch seasons.
- Open episode.
- In a library with more than 100 matching items, change each sort/direction and combine watched/favorite filters; verify the server order and membership across the next page.
- Change filters rapidly, switch library during pagination, leave during loading, then return from Detail; confirm query choices, loading/error/empty states, and current items remain consistent.
- Check the compact sort/filter menus, checked options, current-value summary and active-filter count. Re-selecting a checked option must keep its check and issue no request. Verify Tab/Enter/Space/arrow keys, Esc and outside-click dismissal, focus return, loading-disabled refresh, and reset. At the minimum desktop window size and 125%/150% DPI, titles may ellipsize but toolbar actions and popup choices remain visible.

## 6.5 Search

### Automated

- Empty search does not call API.
- Debounce prevents too many calls.
- Newer search cancels/overrides older search.
- Empty result state appears.
- Error state appears.

### Manual

- Search movie title.
- Search series title.
- Search nonsense string.
- Clear search.

## 6.6 Detail Pages

### Automated

- Movie detail maps title/year/runtime/overview.
- Resume progress shows Continue and Restart.
- Missing backdrop uses fallback.
- Series seasons load.
- Episodes sort correctly and the selected-episode default order is requested, partial progress, first unwatched, then first.
- Episode mapping requests `ImageTags` and derives token-free, tag-encoded 480-pixel thumbnail and 1600-pixel quality-90 Hero URLs from the same Primary tag; a missing tag yields neither URL and makes no extra request.
- Series Hero follows the default, requested, and manually selected episode. Clearing selection during a season change or selecting an episode without an image reveals the series backdrop; non-Series detail continues to use its own backdrop. Derived Hero properties raise change notifications without resetting detail or episode collections.
- The detail header keeps the series backdrop as the lower authenticated-image layer, places a non-focusable/non-hit-testable episode Hero layer above it with placeholders disabled, and preserves the existing readability gradients.
- Quick-number and episode-card tracks bind two-way to the same selected episode; quick numbers only select, while whole-card episode activation plays that card's episode.
- Episode tracks use horizontal recycling virtualization and do not restore the old vertical row navigation.
- Whole-card episode play/continue and Enter play the command-parameter or selected episode and preserve the season/episode return context; the old bottom text action row is absent.
- Series-level primary playback remains based on the independent resume/next recommendation after casual episode selection; explicit card playback still plays only its parameter episode.
- Leaving the detail page cancels pending detail/season/episode loads; late unauthorized results must not clear the active session.
- Center-offset and edge-padding calculations clamp at both ends; selection centering uses layout callbacks without timers or delays.
- Season, quick-number, episode-card, cast/crew, artwork, and Similar rows hide horizontal scrollbars and expose overlaid previous/next buttons. Arrows collapse without overflow and at their unavailable edge, appear only on row hover/focus, and page 95% of the viewport with the Home 220 ms cubic ease-out motion.
- Repeated paging continues from the current animated offset; disabled system animation jumps directly. Selected-episode centering first cancels that track's animation and remains immediate without dynamic margin/padding, forced layout, timers, or delays.
- Only detail navigation carrying a focused episode scrolls the outer page to the episode section; normal series navigation stays at the top.
- Plain wheel input reaches the outer vertical viewer, while Shift + wheel scrolls the horizontal episode track.
- Episode cards use the shared 10 DIP Home card surface, shared watched/progress styles, a visual-only centered play overlay that fades in over 140 ms on pointer hover and appears immediately on keyboard focus, watched/progress and missing-image/overview fallbacks, keyboard focus, and play-oriented accessible names/help text.
- Movie and Series detail map cast/crew from the existing item-detail response in server order, preserve unknown types, limit the list to 24, size/encode portrait URLs without token query data, and do not reload it when episode selection changes.
- Movie and Series artwork map de-duplicated backdrop and Art tags from the existing item-detail response, limit the row to 12 sized URLs without token query data, and make no item-images listing request.
- Cast/crew and artwork appear in that order below the main movie details or series episodes, use authenticated-image placeholders, and hide the whole section when empty. Movie rows have visible ancestors without reserving season/episode layout space. Person cards show portrait/name/one role line without Type and are focusable, invokable buttons; artwork remains informational with distinct automation names and no preview action.
- Similar service sends the required user/image/query fields and canonical Movie/Series type, filters the source item and other media types, preserves order, limits to 12, maps played/resume state, builds sized token-free poster URLs, and handles empty/missing `Items`.
- Similar service covers `/emby` route fallback, timeout, cancellation, network failure, invalid JSON, final 404, 5xx, and distinct 401/403 behavior.
- Movie and Series detail give Similar independent loading/empty/error/success/retry state. Similar failure does not replace main content; 401 clears the session, while 403 stays local and preserves it.
- A newer detail request or leaving Detail cancels Similar and rejects late results. Detail types other than Movie and Series do not request or show the section.
- Similar appears after artwork with authenticated-image fallback, watched/progress state, focus/hover, and previous/next buttons. Opening a card and Back returns to the direct source movie or restores the source series, season, and episode.
- Person profile/works requests cover authentication, route fallback, independent errors, cancellation, missing portrait/biography, empty results, source paging, and deduplication. Person → work → playback/detail → Back preserves the person target; person Back restores its originating detail context. Returning to the same person retains loaded works and scroll position, while replacing the account cancels requests and clears that state.

### Manual

- Open movie detail.
- Open movie with resume progress.
- Open series detail.
- Switch seasons.
- With a season containing at least 50 episodes, use quick numbers and cards to select first, middle, and last episodes; confirm each selected item centers and containers/images are created on demand while scrolling.
- Confirm clicking a quick number only selects, clicking the whole episode card plays that card, and Enter plays the selected episode; confirm no separate bottom play/continue row remains.
- Confirm Left/Right episode selection, visible focus, plain-wheel vertical scrolling, Shift-wheel horizontal scrolling, and keyboard paging for season, cast/crew, artwork, and Similar rows.
- Hover and focus every horizontal row: arrows should appear only when that direction can scroll, never reserve layout space, and never show a right arrow when all quick numbers fit. Repeated arrow clicks should continue smoothly with the same feel as Home; disabling Windows animations should make paging immediate.
- Return from episode playback and confirm the parent series, season, and current episode are restored and centered.
- On Series detail, switch the selected episode and confirm its image replaces only the Hero background while the series title, overview, poster, and top playback recommendation stay unchanged. Switch to an episode without a Primary image and confirm the series backdrop remains visible without a blank flash.
- Open an independent episode detail from a selected series episode, return, and confirm the exact season and episode selection is restored.
- Verify watched badges are independent of the selected highlight, partial progress is visible, and missing thumbnails/overviews remain stable.
- Confirm movie cast/crew and artwork appear below the main details without season/episode controls. Confirm the same rows appear below series episodes, scroll horizontally, survive episode selection without another detail request, and disappear cleanly when the server returns no entries.
- Confirm missing portraits/artwork show placeholders, person cards open the matching profile, and artwork does not open a full-screen preview.
- Activate person cards with mouse and Enter, browse more than 48 works, then open a work and return; confirm the person list and scroll position remain. Return to the originating series and confirm its selected season/episode. Tab should reach person cards and skip informational artwork cards.
- Confirm Similar appears below artwork with no more than 12 cards matching the source Movie/Series type, arrow navigation, poster placeholders, watched/progress state, and visible keyboard focus.
- Confirm an empty Similar result hides the row, a local failure leaves all prior detail content usable and can retry, and opening a Similar card then Back returns to the source movie or restores the exact source series, season, and episode.

## 6.7 Player Controls

### Automated

- Play command maps to player service.
- Pause command maps to player service.
- Seek forward/backward clamps correctly.
- Progress-track direct manipulation jumps on press, follows the captured pointer until release, commits once, and cancels without seeking when capture is lost.
- Volume clamps 0-100.
- Volume-track direct manipulation previews on press, follows the captured pointer, commits once through the existing MPV/settings pipeline on release, preserves direct thumb dragging, restores without committing on capture loss, and shows the muted icon for zero volume or explicit mute.
- The volume popup does not compete for track capture. A real opened WPF Popup presentation proves that tunneled presses on its volume track, visual children, and inline `Run`/`Hyperlink` content remain open; an external inline source closes it without consuming the same event. Moving directly to the progress track closes it without consuming the progress press, and the first seek drag starts normally. The volume trigger still closes without reopening, owner/overlay activation changes do not close it, and whole-application deactivation/unload closes it through a symmetrically removed application event subscription.
- Every interactive Slider instance is inventoried: horizontal seek/default-volume controls and the vertical Player-volume control use the shared captured-track control; scrollbar and read-only progress tracks retain their specialized controls.
- Mute toggles correctly.
- Esc behavior respects fullscreen state.
- Maximized-bound calculation covers taskbars on the top, bottom, left, and right, negative-coordinate monitors, and a work area equal to the full monitor.
- The custom-chrome main window installs and removes its `WM_GETMINMAXINFO` hook, uses the current monitor work area for windowed maximize, and refreshes an already-maximized frame without toggling through Normal.
- Explicit fullscreen continues to use the complete monitor rather than the work area.
- The Player overlay Minimize button delegates to the main-window host command, changes only the host `WindowState`, and shares Close button dimensions, vector-icon, hover/focus, tooltip, and automation conventions.
- Quality tests cover all four choices, cap/device-profile requests, server errors and no silent uncapped fallback, position/state/track preservation, obsolete events, single stopped reporting, replacement-load recovery, canceled navigation, and capped-stream seek renegotiation.
- Playback-information reads distinguish MPV output from source metadata, display unavailable values honestly, and leave playback state/reporting unchanged.

### Manual

- Play video.
- Pause/resume with button.
- Pause/resume with Space.
- Press an empty progress-track position, keep holding while dragging in both directions, and verify the thumb jumps immediately, follows smoothly, and seeks once on release.
- Drag the progress thumb directly and verify its existing release-to-seek behavior remains unchanged.
- Seek with Left/Right.
- Change volume with buttons and Up/Down.
- Press the empty volume track, keep holding while dragging both directions, and confirm the preview changes immediately and smoothly before one release commit; repeat by dragging the thumb directly, then lose capture outside the popup and confirm the prior value is restored.
- Leave the volume popup open after a drag, then press and hold an empty progress-track position. Confirm that same first press closes the popup, immediately moves the seek thumb, and continues dragging without requiring a second attempt.
- Drag to zero and toggle mute at a non-zero volume; confirm both show the muted icon while retaining the correct underlying volume/mute behavior.
- Toggle mute.
- Toggle fullscreen with F.
- Exit fullscreen with Esc.
- Exit player with Esc when not fullscreen.
- Maximize the browsing window and start playback; confirm the Windows taskbar stays visible and every bottom player control remains fully above it.
- From that state, enter explicit fullscreen with `F` or double-click; confirm the taskbar is covered, then press Esc and confirm the original taskbar-safe maximized state is restored.
- Repeat windowed maximize on monitors with different DPI and with taskbars placed on each screen edge; include a negative-coordinate secondary monitor when available.
- Minimize from restored, windowed-maximized, paused, playing, and explicit-fullscreen states; restore from the taskbar and confirm the same Player page, playback state, and prior maximize/fullscreen restore behavior remain intact.
- On a server that permits transcoding, switch Original → 1080p → 720p → 480p → Original during playback and while paused. Confirm near-continuous position, pause/fullscreen/volume/mute/track retention, progress reporting, seek after transcode, and safe exit during switching. Repeat with rejected/failed transcoding and verify retry or original-quality recovery. Record actual output separately from requested caps.
- Open and refresh playback information for direct and transcoded playback; compare actual video resolution/codec with source metadata, and confirm missing fields never display fabricated values or label source bitrate as live throughput.

## 6.8 Subtitles

### Automated

- Subtitle list maps from media streams.
- `关闭字幕` exists.
- Selecting subtitle calls player service.
- Missing subtitles shows disabled/empty menu.
- Adjustment tests cover delay/scale/position steps and clamps, invariant MPV property values, reset, successful-only UI updates, failure without stopping playback, cancellation/stale instances, preservation through quality reload, and reset for a new item.

### Manual

- Play item with no subtitles.
- Play item with embedded subtitles.
- Switch subtitle.
- Disable subtitle.
- Confirm subtitles do not overlap permanently with controls.
- Adjust delay, size, and position on a text-subtitle sample, reset, switch quality, and open another item. Confirm the documented temporary lifetime. Try bitmap/styled or burned-in subtitles when available and confirm the UI explains their limitations rather than claiming all controls alter rendered output.

## 6.9 Audio Tracks

### Automated

- Audio list maps from media streams.
- Selecting audio calls player service.
- Single audio track case is handled.

### Manual

- Play item with one audio track.
- Play item with multiple audio tracks.
- Switch audio.
- Confirm menu indicates current track.

## 6.10 Playback Progress Sync

### Automated

- Start report occurs after playback begins.
- Progress report throttles to configured interval.
- Pause report includes paused state.
- Stop report includes current position.
- Failed progress report does not throw to UI.
- Playback-report 401 clears the expired session and returns to login.
- Playback-report 401 still stops playback, clears the runtime session, shows the friendly expiry message, and returns to Login if persistent credential removal fails; the cleanup failure is logged and does not leave expiry handling stuck.
- Playback-report 403 preserves the session and local playback.
- Timeout, server-unreachable, and 5xx report failures preserve local playback; caller cancellation is silent.
- Repeated recoverable report failures show at most one non-blocking warning per playback item.
- A blocked in-flight progress report completes before the single stopped report; progress work queued before or after closing never reaches the server after stopped.
- Player exit uses one final position/runtime snapshot for both the stopped request and Detail navigation, and navigation occurs after the report attempt and local player stop.
- If completion starts Stopped before Back, Back awaits that same immutable outcome; a late player-progress event changes neither the stopped request position nor the returned Detail position, and Stopped remains at-most-once.
- Movie and episode detail merge a returned 50% snapshot immediately, retain it across a stale detail/episode response, and use it for the next playback request without waiting for server refresh.
- A lower local position after seeking backward wins over a higher stale server position. Same-item navigation without an explicit playback state clears the pending snapshot and sync warning, and a canceled older generation cannot restore them.
- Applying an episode return state immediately refreshes the series primary action, recommendation text, resume summary, and their binding notifications.
- A recoverable stopped-report failure returns to Detail with the local snapshot and a non-blocking sync warning; a 401 still returns only to Login.
- A returned snapshot at the existing completion threshold displays watched state and does not offer resume.
- Seconds-to-ticks conversion is correct.

### Manual

- Start video and watch for at least 20 seconds.
- Stop video and reopen it.
- Confirm Continue resumes near stopped position.
- Exit an episode midway and confirm the parent series, season, and episode remain selected, the card immediately shows progress, and clicking it resumes from that position even if the server UI has not refreshed yet.
- Pause and resume.
- Finish video past 90%.
- Confirm Emby marks as watched or moves out of continue-watching as expected.

## 6.11 Favorites / Played State, P1

### Automated

- Favorite true calls correct API.
- Favorite false calls correct API.
- API failure rolls back optimistic UI or shows final error.
- Mark played/unplayed maps correctly.

### Manual

- Favorite an item.
- Refresh page.
- Unfavorite item.
- Mark played.
- Mark unplayed.

## 6.12 Settings

### Automated

- Settings persist.
- One `FileAppSettingsService` instance serializes reads and complete read-modify-write operations so concurrent setters do not lose unrelated fields.
- Settings writes use a same-directory unique temporary file and atomic replace/move; cancellation before replacement leaves the primary unchanged and removes the temporary file.
- A second successful save keeps the prior primary as `settings.json.bak`; a missing, empty, malformed, or unreadable primary recovers from a valid backup without replacing that backup with corrupt data.
- When both primary and backup are malformed, reads return defaults and preserve both files for diagnosis.
- Settings-page preference updates and player last-volume updates merge atomically, and concurrent first-run device ID requests return one stable value.
- A failed first device-ID save releases the single-flight gate so the next request retries and persists a stable value.
- File settings serialization is an in-process, single-service-instance guarantee; cross-process writers remain outside the 1.0 automated contract.
- Seek step setting affects seek command.
- Default subtitle language is read by player setup.
- Logout clears secure storage.
- Settings logout and switch-server buttons open the shared confirmation overlay without changing session state before confirmation.
- Confirmation defaults keyboard focus to Cancel, Esc cancels, and repeated confirmation cannot start concurrent cleanup.
- Dirty confirmation explains that unsaved settings will be discarded; cancel keeps the draft, success discards it, and authentication cleanup failure keeps it on Settings.
- Logout success opens Login while preserving the server; switch-server success opens Server Connection after clearing it.
- If switch-server clears authentication/runtime state but cannot clear the saved address, Server Connection opens with the retained editable address and a partial-cleanup warning.
- Confirming an account action cancels and invalidates an in-flight media-library scan; late success or 401 results cannot override the resulting navigation.
- A current media-library scan 401 clears the session and opens Login despite dirty settings or an open account confirmation, including when persistent credential removal fails.
- Cache tests cover actual byte/file measurements, empty usage, separate clears, confirmation/retry/busy states, preserved preferences/credentials, decoded-image invalidation, old downloads/builds rejected after clear, serialized disk hydration/deletion, owned-file-only deletion, canceled rebuild recovery, and current versus stale 401 results.
- Real WPF tests exercise usage bindings, Enter/Esc, confirmation focus, a scrollable small-window layout, and loaded/detached image reload after clearing.

### Manual

- Change subtitle language.
- Change audio language.
- Change seek step.
- Export logs.
- Read cache usage, cancel and confirm each clear, then rebuild the current account's index. Verify usage updates, searches rebuild without old results, images reload, preferences/login remain, and storage/network failures offer retry. Leave Settings during rebuild and check that no late navigation or write replaces the new state.
- With and without unsaved changes, cancel and confirm both account actions using mouse, keyboard, and Esc.
- Force a local credential cleanup failure and confirm the page and draft remain available with a friendly error.
- Force only saved-address cleanup to fail after session cleanup and confirm Server Connection opens with the retained editable address and partial-cleanup warning.
- On Windows, fault a settings write, flush, and replace in turn and confirm no orphan `settings.json.<pid>.<guid>.tmp` remains; this stays an integration gate rather than adding a filesystem abstraction to unit tests.

## 6.13 Internal Release Pipeline

### Automated

- `global.json` selects the approved SDK feature band; every source/test project has a current `packages.lock.json`, and every source project participating in publish has a current `packages.win-x64.lock.json`.
- Release restore runs in locked mode; build and all four test projects run in Release against an isolated artifacts path under a new `.tmp/release-staging` target.
- Public unconditionally rejects `-AllowDirty`, dirty worktrees, skipped tests, and skipped launch smoke before creating staging. `-AllowDirty` and `-SkipTests` are Internal-only, and the manifest records both dirty and test-skip state.
- The repository sensitive scan covers current tracked plus non-ignored untracked text files with explicit text-extension, 4 MiB fail-closed, NUL rejection, and exact fixture-exclusion rules. Repository and payload files must have a complete non-reparse ancestor chain; payload traversal rejects every reparse file or directory before recursive scan, hashing, or ZIP creation. After publish, every text config/deps/runtimeconfig/manifest is scanned again; PDB is prohibited from payload rather than pretending DLL/PDB internals were scanned.
- OutputRoot must remain a strict workspace child. Each run uses a unique `.working-<guid>` directory and temporary ZIP; failures remove only those exact validated paths. Final placement occurs only after all checks, with rollback or a `RELEASE-FAILED.txt` marker if the second move fails.
- The MPV input manifest, DLL size, SHA256, FileVersion, and manifest architecture must match before publishing and again in the payload; independently, the actual PE machine must always be AMD64 for win-x64.
- Public MPV license evidence must consist of non-reparse regular files inside the workspace, each with manifest size/SHA256. Evidence is copied without flattening to `third-party/mpv/` and rehashed in the payload before `distributionReady=true` is possible.
- Public release fails while MPV source URL, build-recipe revision, and license evidence are incomplete. Internal release remains `distributionReady=false`.
- Publish output contains the app EXE, all five application DLLs, `libmpv-2.dll`, and `mpv-runtime.json`.
- Launch smoke starts only the exact staging EXE, requires a visible top-level window, and closes or, only if required, kills that exact PID.
- The release manifest records deployment/runtime requirements, Git state, SDK, MPV status, launch-smoke result, and sorted SHA256/size entries for every publish payload file.
- ZIP creation never calls the broad build cleanup or the development run script and never writes standard `bin\Release`.
- Behavior tests inject repository/payload NUL candidates, file and directory reparse points, Public skip flags, a post-publish PDB, and command failures; each gate must fail nonzero without leaving an owned `.working-*` transaction or final release target.

### Manual

- Produce a FrameworkDependent Internal ZIP from a dirty verification tree using `-AllowDirty`; inspect `release-manifest.json`, confirm `workingTreeDirty=true` and `distributionReady=false`, then launch the extracted app on a machine with x64 .NET 8 Desktop Runtime.
- Produce a SelfContained Internal ZIP and confirm it launches without a separately installed .NET Desktop Runtime when the local SDK/runtime packs are available.
- Confirm Public + `-AllowDirty` and Public dirty-tree preflights fail without creating OutputRoot; separately confirm incomplete MPV provenance/license evidence still fails before restore/publish.
- Inject a failing `dotnet` command and confirm the nonzero exit propagates while only the current `.working-*` paths are removed.
- Treat launch smoke only as startup verification; separately test real server login, movie/episode playback, subtitles, audio tracks, progress sync, and fullscreen behavior.

## 6.14 Daily Fixed Deployment

### Automated

- `scripts/deploy-daily.ps1` holds a `FileStream`/`FileShare.None` lock in the InstallRoot across build, test, smoke, promotion, and cleanup. A real concurrent holder must make the contender fail quickly without touching shared install state; the retained lock file and every ancestor must be non-reparse.
- The entry invokes the existing release pipeline as tested SelfContained Internal with visible-window smoke, `-DirectoryOnly`, and the Internal-only non-force smoke mode. It must not create a disposable ZIP, pass `-SkipTests` or `-SkipLaunchSmoke`, write standard `bin\Release`, or hardcode a machine-specific install path. Formal release defaults still produce the immutable ZIP and Public rejects the non-force mode.
- Formal and daily smoke start only their owned executable with `--release-smoke`. Policy tests prove this exact marker bypasses the settings close guard while an ordinary launch and lookalike arguments still use it; publish passes the marker, then `CloseMainWindow` can end the dedicated instance naturally.
- A real unresponsive smoke fixture proves the daily-safe cleanup branch leaves the exact process alive and reports its PID after graceful close fails; this failure must occur before promotion. Daily code must never transitively reach force termination.
- Payload promotion rejects PDB, invalid/mismatched manifests, reparse files/directories or ancestors, filesystem-root targets, and a running `JebyPlayer` or legacy `EmbyPlayer.App`; it never calls a broad process stop.
- First promotion creates only `current`. A second promotion leaves the new payload in `current`, the former payload in `previous`, and no `.next-*`, `.rollback-*`, or `.previous-old-*` transaction directory.
- Inject failure on the second directory move (`.next-*` to `current`) and prove the original `current` is restored, no false `previous` is created, and owned transaction paths are cleaned.
- On a third deployment, inject partial deletion failure for `.previous-old-*` after the new `current` and healthy `previous` are committed. Prove the healthy pair remains, the damaged oldest copy is retained only as transaction evidence, and no rollback replaces `previous`.
- The unique workspace `.tmp\daily-deploy\.daily-*` build root is removed after both a successful operation and an injected failure.

### Manual

- With the player closed, deploy twice to an isolated workspace `-InstallRoot`; launch `current\JebyPlayer.exe`, then verify `previous` is the preceding payload and there is never more than one previous copy.
- Keep the installed player open and retry deployment. Confirm the command gives a friendly close-and-retry error, leaves `current` untouched, and does not stop the process.
- Hold `.daily-deploy.lock` from another process and retry the same InstallRoot. Confirm the contender fails before build and the holder's state remains untouched.
- Force the daily smoke process to ignore its close request. Confirm deployment reports the still-running PID, does not force-stop it, and does not promote a new `current`.
- During ordinary script failures, confirm best-effort rollback restores `current`. Record that Windows cannot atomically exchange two non-empty directories: there is a very short rename window, and power loss or forced termination can leave a transaction directory that the next run deliberately refuses for manual inspection.
- Use the immutable versioned release directory/ZIP workflow in 6.13 for formal RC/Release artifacts; do not treat `current`/`previous` as archival releases.

## 6.15 Current Playback and Library Enhancement Verification

Run affected projects sequentially from the repository root, using a dedicated workspace output and writable temporary directory. These commands are runnable guidance, not a claim of passing results:

```powershell
$verificationRoot = Join-Path (Get-Location) '.tmp/feature-wave/verification'
New-Item -ItemType Directory -Force (Join-Path $verificationRoot 'temp') | Out-Null
$env:TEMP = Join-Path $verificationRoot 'temp'
$env:TMP = $env:TEMP
$artifactsPath = Join-Path $verificationRoot 'artifacts'
dotnet test tests/EmbyPlayer.Emby.Tests/EmbyPlayer.Emby.Tests.csproj --artifacts-path $artifactsPath --filter "FullyQualifiedName~EmbyPlaybackQualityTests|FullyQualifiedName~EmbyPersonServiceTests|FullyQualifiedName~EmbyCacheManagementServiceTests|FullyQualifiedName~LocalMediaSearchIndexTests|FullyQualifiedName~EmbyImageServiceTests"
dotnet test tests/EmbyPlayer.Player.Tests/EmbyPlayer.Player.Tests.csproj --artifacts-path $artifactsPath
dotnet test tests/EmbyPlayer.UI.Tests/EmbyPlayer.UI.Tests.csproj --artifacts-path $artifactsPath --filter "FullyQualifiedName~PlayerViewModel|FullyQualifiedName~Person|FullyQualifiedName~Cache|FullyQualifiedName~AuthenticatedImageTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsPageVisualTests"
```

Review each exit code before continuing. Final integration also requires the normal solution build/regression gate and affected WPF/real-playback checks. Fake HTTP/native tests and offscreen WPF tests do not establish real-server transcoding support, subtitle rendering, or subjective playback continuity.

## 6.16 Session Queue, Local Watch Later, and Shortcut Configuration

These three features are implemented in the development workspace and await the main task's integration acceptance. The checks below are verification targets, not a claim that all have passed. Keep their results separate from earlier releases and the five-feature batch in section 6.15.

### Automated

- Queue service: accept only Movie/Episode, deduplicate current/pending, move to any valid index, remove, clear pending without clearing current, and peek/consume in the reordered sequence. Snapshots remain immutable and concurrent additions do not lose items.
- Queue session boundaries: a normalized same server/user retains state; logout, different account/server, or restart starts empty. A stale playback completion cannot consume an item in a new session, including an identical media ID.
- Queue lifecycle: candidate stays pending until native Ready; preparation and replacement-load failures do not consume or skip it. Repeated Ready/EOF/Stopped and late events preserve one replacement/reporting lifecycle. Manual next works with queue continuation off. Pending queue continuation takes priority over automatic next episode; pending with continuation off suppresses that fallback; empty pending uses the saved next-episode setting, whose default remains on.
- Queue interaction: actual WPF keyboard play/up/down/remove/clear and position selection update the service; Back/close cancels preparation; external player transitions disable queue editing without canceling their own callback. Embedded Back is hidden, long titles trim, and the final row remains reachable in a small popup. Merely opening/closing the list does not stop native playback.
- Watch Later storage: add deduplicates and orders newest first; remove and restart persist; normalized server/user isolation survives logout/relogin. List files contain neither credentials nor playback URLs and remain outside cache-clearing targets. Favorite/played state and the session queue are unchanged by local list mutations.
- Watch Later recovery: valid backup recovery, cancellation during writes, failed reads/removals with accurate retry behavior, no stale page/account completion, and confirmed reset of only a damaged active-account list while preserving damaged copies. A stale remove/reset request cannot mutate the new account.
- Watch Later WPF/navigation: detail save state changes only after storage success; Series saving preserves the Series entry, while Add to Queue resolves an Episode. Cards open details, Back retains the list/scroll, and remove/reset controls have loading, error, focus, and small-window reachability checks.
- Shortcuts: validate all 11 defaults, exact Ctrl/Shift/Alt combinations, duplicate gestures, reserved/system keys, clear, restore defaults, legacy settings, invalid saved configuration fallback, and successful persistence. A failed save retains the draft and old effective preferences.
- Shortcut WPF/player routing: real recording buttons support conflict correction, Esc/focus-loss cancellation, and Save; text/password input and focused player popups retain their own keys. Custom bindings replace old actions and update help/tooltips. Esc keeps menu → fullscreen → return priority. Audio/subtitle actions use existing track guards and progress reporting.

Focused tests are in `PlaybackQueueServiceTests.cs`, `PlaybackQueueViewModelTests.cs`, `PlaybackQueuePageRuntimeTests.cs`, `PlayerQueueIntegrationTests.cs`, `FileWatchLaterStoreTests.cs`, `WatchLaterViewModelTests.cs`, `WatchLaterPageRuntimeTests.cs`, `PlayerShortcutBindingsTests.cs`, `ShortcutSettingsViewModelTests.cs`, `SettingsShortcutRuntimeTests.cs`, and `PlayerViewModelShortcutTests.cs`, plus affected detail, shell, settings, and player tests. The queue/player shortcut files extend `PlayerViewModelTests`; the Settings shortcut runtime file extends `SettingsCacheRuntimeTests`, so use those actual class names when filtering. Use the repository verification commands with isolated outputs under `.tmp/personal-playback`; inspect each actual result before recording success.

### Manual / Integrated Runtime

- Add a movie and a selected series episode, reorder them in Home and the Player popup, then verify manual next and natural EOF follow that order exactly once. Confirm current/pending state, actual video output, position reporting, and Back behavior after each replacement. Test queue continuation off with pending items, then an empty queue with automatic next episode on/off.
- While preparing a queued item, close its popup, return, switch account, or provoke a server/native-load failure. Confirm old playback remains intact on preparation failure, the pending candidate is not lost on load failure, errors are recoverable, and no canceled/obsolete response starts another media item.
- Save/remove Movie, Series, and Episode entries, restart the app, switch server/account and return. Verify local list isolation, list/detail return state, independent Favorite state, and retention after cache clearing. Use isolated fixture files for unreadable/corrupt-list recovery; never damage the user's list to test it.
- Record a modifier combination, resolve a conflict, clear an action, restore defaults, save and restart. Exercise the saved binding in real playback and verify the old key no longer performs that action. Confirm typing in search/other text fields is unaffected, popup navigation works, and Esc preserves the reserved ordering.

Offscreen WPF and fake playback tests establish control/state behavior, not actual server playback continuity. Record real-media coverage and any untested boundaries separately. Build the current runnable application before offering its test entry; do not present a prior build as this batch's output.

### Keyboard seek and loading responsiveness

- Tap the configured seek keys for one configured step; hold for progressively faster target movement and release for one native seek. Test both directions, timeline bounds, playing and paused media, and a slow or failed seek without queued repeats.
- Cancel a held seek by opening a menu, switching focus, entering/leaving fullscreen or mini mode, or leaving/replacing playback. Old key releases must not seek the new item. Verify custom bindings and text/popup keyboard handling.
- Automatic login must not wait for the hidden recent-server list. Initial connection and later server-switch pages must still populate that list; expired-token, permission and network routing remain covered.
- After Home discovers libraries, independent content rows load concurrently with the original request count and query semantics. Verify cancellation, authentication failures and optional section errors. Synthetic latency results must be labelled as such, not presented as real-server startup timings.
- Slow native initialization, seek and stop must leave the calling UI thread responsive. Immediate load/stop and replacement retain lifecycle order; cancellation after handle detachment must still retire the old handle. Exercise real MPV with isolated local media as well as deterministic blocking-native tests.

## 7. Regression Checklist Before 1.0 Release

- Fresh install starts correctly.
- No server → connection page.
- Valid server → login page.
- Login success → home page.
- Restart → auto-login.
- Logout → login page.
- Switch server from Settings → confirmation → empty connection page; switch server from Login → empty connection page without a second confirmation.
- Movie library browsing works.
- TV library browsing works.
- Movie detail works.
- Series detail works.
- Movie playback works.
- Episode playback works.
- Subtitles work.
- Audio tracks work.
- Progress sync works.
- Search works if included.
- Offline server handled.
- Token expired handled.
- Empty library handled.
- Missing images handled.
- Logs do not contain token/password.
- Locked Release restore/build/tests pass in an isolated staging path.
- MPV runtime input and published payload hashes match the tracked manifest.
- Internal ZIP manifest is retained for traceability; Public stays blocked until MPV provenance and license evidence are complete.
- Build passes.
- Tests pass.

## 8. Bug Triage Rules

Severity:

| Severity | Definition |
|---|---|
| S0 | app cannot start, data/security issue, impossible to play anything |
| S1 | core feature broken: login, browse, play, progress sync |
| S2 | important but workaround exists: subtitle menu wrong, search bug |
| S3 | visual polish or minor edge case |

1.0 cannot ship with known S0 or S1 bugs.

## 9. Codex Review Checklist

After Codex implements a task, ask it to review its own diff for:

- unrelated file changes
- missing tests
- UI thread blocking
- swallowed exceptions
- missing error states
- token/password leaks
- direct API calls from UI
- direct MPV calls from UI
- progress report spam
- missing cancellation tokens
- stale async result bugs
