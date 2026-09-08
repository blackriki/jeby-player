# Jeby Player 1.0 Requirements

## 1. Document Info

| Field | Value |
|---|---|
| Product | Jeby Player |
| Version | 1.0 MVP |
| Target Platform | Windows 10 / Windows 11 |
| Default Tech Stack | C# + WPF + MPV |
| Architecture | MVVM |
| Main User | Emby Server user who wants a native Windows player |
| Core Principle | Stable playback first, complete Emby feature clone second |

The approved cache management, subtitle adjustment, playback information, person-page, and quality-selection work is implemented on `codex/playback-and-library-enhancements` and awaits final integration acceptance. These requirements do not declare that development branch released.

The subsequently approved session playback queue, local Watch Later list, and configurable player shortcuts are also implemented in the development workspace and await the main task's integration acceptance. They are not declared deployed by this specification.

## 2. Product Positioning

Jeby Player is a native Windows desktop client for connecting to a user's own Emby Server.

It should provide a user experience similar to Emby Web for browsing and watching media, but it should behave like a proper desktop player: fast launch, stable playback, predictable shortcuts, clean subtitles/audio switching, and reliable playback progress sync.

This project is not an Emby Server management tool.

## 3. 1.0 Goals

1. A user can connect to an Emby Server with a server URL.
2. A user can log in with an Emby username and password.
3. The app can securely save the access token and auto-login next time.
4. The app can show home content, libraries, media posters, and media details.
5. The app can play movies and episodes using MPV.
6. The player supports core playback controls, fullscreen, subtitles, audio tracks, and progress seek.
7. The app can sync playback progress back to Emby Server.
8. The app can search media.
9. The app can handle common errors without crashing.
10. Codex can implement the app in small tasks using this documentation.

## 4. Non-Goals for 1.0

Do not implement these in 1.0 unless explicitly approved:

- Emby Server dashboard/admin pages
- user management
- automatic, background, or scheduled library scanning
- metadata editing
- plugin management
- Live TV
- DVR
- downloads/offline mode
- casting / remote control
- Emby Connect cloud login
- music player mode
- photo gallery mode
- multi-theme system
- mobile/tablet layout
- direct webview wrapper of Emby Web

## 5. User Types

### 5.1 Normal Emby User

Uses the app to browse and play movies/TV episodes from their Emby Server.

Needs:

- login
- browsing
- search
- playback
- progress sync
- subtitle/audio control

### 5.2 Household Shared User

Uses the same Windows app but may switch account or server.

Needs:

- logout
- switch server
- separate token/session per account

### 5.3 Admin User

May log in as an Emby admin, but 1.0 must not expose server administration features except an explicit user-initiated media library scan. The app must never start a scan automatically, in the background, or on a polling schedule.

Needs:

- same playback experience as normal user

## 6. Priority Definitions

| Priority | Meaning |
|---|---|
| P0 | Must ship in 1.0 |
| P1 | Should ship if stable and low risk |
| P2 | Backlog, not required for 1.0 |

## 7. Feature Priority Summary

### P0

- server connection
- username/password login
- token save / auto-login
- logout
- home page
- library browsing
- movie detail page
- series/season/episode detail flow
- MPV video playback
- playback controls
- subtitles switching
- audio track switching
- playback progress sync
- continue watching
- loading / empty / error states
- basic settings
- logs for troubleshooting

### P1

- search
- favorite / unfavorite
- mark played / unplayed
- sorting and filtering
- auto-play next episode
- playback quality selection
- recent servers list
- cache usage, clearing, and search-index rebuilding
- temporary subtitle timing, size, and position adjustment
- playback information and actor/person pages
- session playback queue, account-scoped local Watch Later, and configurable player shortcuts

### P2

- downloads
- Live TV / DVR
- remote control
- casting
- recommendation surfaces beyond the approved Movie/Series-detail Similar rows
- advanced HDR/audio passthrough settings
- custom themes
- controller/TV mode

## 8. Global Acceptance Criteria

The 1.0 release is acceptable only when:

- A clean install can connect to an Emby Server.
- A user can log in and reopen the app without logging in again.
- A user can browse movies and TV shows.
- A user can open details for movies, series, seasons, and episodes.
- A user can play at least one movie and one episode.
- A user can pause, resume, seek, adjust volume, mute, and fullscreen.
- A user can switch subtitles and audio tracks when available.
- Playback progress appears in Emby continue-watching behavior after playback.
- Empty libraries do not crash the app.
- Expired token returns the user to login.
- Offline server shows a friendly error.
- App build and core tests pass.

## 9. Main User Flows

### 9.1 First Launch Flow

```text
Launch app
↓
Check saved server and token
↓
No saved server
↓
Show Server Connection Page
↓
User enters server URL
↓
App checks server connectivity
↓
Success → Login Page
Failure → show error and stay on page
```

### 9.2 Login Flow

```text
Login Page
↓
User enters username/password
↓
App authenticates with Emby Server
↓
Success → save token securely → Home Page
Failure → show error and stay on login page
```

### 9.3 Auto-Login Flow

```text
Launch app
↓
Saved server + saved token exist
↓
App validates token by requesting current user or libraries
↓
Valid → Home Page
Invalid / expired → clear token → Login Page
```

### 9.4 Movie Playback Flow

```text
Home or Library Page
↓
User clicks movie card
↓
Movie Detail Page
↓
User clicks Play or Continue
↓
App requests PlaybackInfo
↓
Player Page opens
↓
MPV starts playback
↓
App reports progress every 5-10 seconds
↓
User exits playback
↓
App reports stopped position
↓
Return to previous page
```

### 9.5 TV Episode Playback Flow

```text
Series card
↓
Series Detail Page
↓
User selects season
↓
User selects episode
↓
Episode detail or direct play
↓
Player Page
↓
At natural completion, follow pending queue items first; with no pending items, use the auto-play-next-episode setting
```

## 10. Functional Requirements

## 10.1 Server Connection

### Goal

The user can connect the app to an Emby Server by entering a server URL.

### UI

- Server URL input.
- Connect button.
- Loading indicator.
- Error message area.
- Recent successful servers, P1.

### Behavior

- Accept `http://` and `https://` URLs.
- Trim whitespace.
- If protocol is missing, show validation guidance or auto-prefix `http://` only if the UX explicitly supports it.
- Normalize trailing slashes.
- Verify the server using a public system endpoint where possible.
- Save last successful server URL.
- Keep up to five recent successful server addresses, newest first, with normalized duplicates merged. Only server addresses are stored in this list.
- Selecting a recent server fills the address input; the user still clicks Connect. Each entry can be removed without clearing the selected server or authentication.
- Preserve recent servers across logout and switch-server. Switch-server still clears the address input; it does not automatically select a history entry.
- Existing settings containing only the last server address populate the initial history. Removing that entry must remain effective after restarting.

### Acceptance Criteria

- Given no saved server, when the app starts, then it shows the Server Connection Page.
- Given an empty URL, when the user views the page, then the Connect button is disabled.
- Given an invalid URL, when the user clicks Connect, then the app shows `请输入有效的服务器地址`.
- Given a valid reachable Emby Server, when the user clicks Connect, then the app navigates to Login Page.
- Given an unreachable server, when the user clicks Connect, then the app shows `无法连接服务器` and stays on the page.
- Given a timeout, when connecting, then the app shows `服务器连接超时`.
- Given recent servers, selecting one fills the input without making a network request; a successful connection moves its normalized address to the front.
- Given a failed local history read or write, the page shows a retryable error. A failed removal keeps the entry visible; a failed save after connection stays on the connection page.

### Out of Scope

- LAN discovery.
- QR code login.
- Emby Connect login.
- advanced certificate management.

## 10.2 Authentication

### Goal

The user can log in with an Emby username and password.

### Behavior

- Authenticate using username/password.
- Save returned access token securely.
- Save user ID and display name locally.
- Do not store password.
- Support logout.
- Logout clears the secure authentication token and current runtime user session, but preserves the last server, stable device ID, playback preferences, and search history.
- Switch server clears authentication first, then the runtime session, then the saved server address. It preserves the stable device ID and general preferences/history.
- Support token expiry handling.

### Acceptance Criteria

- Given a valid server and valid credentials, when the user logs in, then Home Page opens.
- Given wrong credentials, when the user logs in, then the app shows `用户名或密码错误`.
- Given the server is unreachable, when the user logs in, then the app shows `服务器不可用`.
- Given a saved valid token, when the app starts, then it opens Home Page automatically.
- Given a saved expired token, when the app starts or any API returns 401, then the app clears token and opens Login Page.
- Given the user logs out, then token and current user data are cleared.
- Given the user logs out from Settings, then the action is confirmed, unsaved settings are explicitly discarded, and Login Page opens with the last server still selected.
- Given the user switches server, then the action is confirmed in Settings (or runs directly from Login), authentication and the saved server are cleared, and Server Connection Page opens without a stale address.
- Given authentication cleanup fails before the runtime session is cleared, then the user stays on the current page with a friendly retryable error.
- Given the secure token is deleted but local session-metadata cleanup fails, then the runtime session is cleared and the app opens a safe Login or Server Connection page with an accurate warning.
- Given switch-server authentication and runtime cleanup succeed but clearing the saved server address fails, then Server Connection Page opens with the retained address editable and a clear partial-cleanup warning.

## 10.3 Home Page

### Goal

The Home Page gives quick access to continue watching, recently added media, and media libraries.

### Required Sections

- Continue Watching.
- Recently Added.
- Movie libraries.
- TV libraries.
- Favorites, P1.

### Behavior

- Use horizontal media rows.
- Load sections independently where possible.
- Show skeleton/loading state while content loads.
- Empty sections may be hidden or show a light empty state depending on importance.
- Clicking a card opens its detail page.
- Hovering a playable card shows quick play affordance.
- Continue Watching, Recently Added, and every media category row have the same working `查看全部` action. Each opens its complete section as a paginated poster grid, with refresh, empty/error/retry states, and a return-to-Home action.
- Complete sections preserve the Home query scope: resumable videos ordered by last playback, grouped latest items, all movies, all series, all classified animation libraries, or all box sets. They are not limited to the Home preview's 24 cards.

### Acceptance Criteria

- Given a logged-in user, when Home Page loads, then it displays at least library entry points.
- Given resume items exist, when Home Page loads, then Continue Watching displays them with progress bars.
- Given no resume items, then Continue Watching is hidden or shows `暂无继续观看`.
- Given recently added items exist, then Recently Added displays poster cards.
- Given any Home section, when `查看全部` is activated with mouse or keyboard, then its matching full list opens and more items can be loaded beyond the Home preview.
- Given a full-list item is opened, then Back returns to the same section with its loaded pages retained; episode details retain their series/season/episode context.
- Given a section fails to load, then other sections still load if possible.

## 10.4 Library Browsing

### Goal

The user can browse movie and TV libraries.

### Required Media Types

- Movie.
- Series.
- Season.
- Episode.
- Collection, P1.

### Movie Library Requirements

- Poster grid.
- Title.
- Year if available.
- Watch progress if available.
- Favorite badge if available.
- Watched indicator if available.
- Pagination or incremental loading.
- Sorting, P1.
- Filtering, P1.

### TV Library Requirements

- Series grid.
- Series detail page.
- Season selector.
- Episode list.
- Episode title, number, overview, runtime, progress.
- Series-level cast/crew, artwork, and a Similar row limited to 12 recommendations.

### Acceptance Criteria

- Given a movie library, when the user opens it, then the app shows movie cards.
- Given many movies, when the user scrolls, then more items load without freezing the UI.
- Library sorting supports name, date added, and year in either ascending or descending order. Watched filtering supports all/unwatched/watched and combines with all/favorites-only. Defaults are name ascending with both filters set to all.
- Sorting and filtering apply to the complete server query, with bounded pagination; changing an option starts at the first page. The same option value does not issue another request.
- Returning from details in the same login session retains library query options. Logging out, switching server/account, or replacing the session clears retained library content and restores default query options; options are not persisted across app launches.
- A superseded or abandoned library request must not change content, errors, pagination, or the active session. Refresh may retain prior content only for the same library and query.
- Given a missing poster, then a placeholder appears.
- Given a TV series, when the user opens it, then seasons are shown.
- Given a season, when selected, then episodes are shown in order.
- Given a series with similar items, when its detail loads, then up to 12 similar Series cards appear after artwork without blocking the rest of the detail page.
- Given no similar items, then the whole Similar section is hidden; given a Similar request failure, then only that section shows a friendly retry state.

## 10.5 Search

### Goal

The user can search media by keyword.

### Behavior

- Search input in top navigation or dedicated page.
- Debounce search input by about 300 ms.
- Do not search for empty or 1-character terms unless explicitly submitted.
- Show results grouped by type if possible.
- Show loading, empty, and error states.

### Acceptance Criteria

- Given a keyword matching movies, when searched, then matching movies appear.
- Given a keyword matching series, when searched, then matching series appear.
- Given no results, then show `没有找到相关内容`.
- Given a network error, then show retry UI.

## 10.6 Media Details

### Goal

The user can inspect a media item and choose how to play it.

### Movie Detail Page

Show:

- backdrop background
- poster
- title
- original title if available, P1
- year
- runtime
- community rating if available
- genres
- overview
- play button
- continue button if resume position exists
- restart button if resume position exists
- favorite button, P1
- played/unplayed button, P1
- audio/subtitle summary if available, P1
- movie-level cast and crew, when returned by the item-detail response
- movie-level artwork thumbnails from existing backdrop and Art metadata
- a Similar row of up to 12 movie recommendations

### Series Detail Page

Show:

- backdrop background
- poster
- title
- year range if available
- overview
- season selector
- episode list
- series-level cast and crew, when returned by the item-detail response
- series-level artwork thumbnails from existing backdrop and Art metadata
- a Similar row of up to 12 series recommendations
- next unwatched episode shortcut, P1

Cast/crew cards on movie and series details open the selected person by server ID. The person page shows a portrait, name, biography, and paginated Movie/Series works available to the current account. Opening a work and returning restores the person list and scroll position; returning from the person page restores the originating detail context, including the selected season/episode. Portrait, biography, and empty-work fallbacks remain usable. Artwork stays a lightweight thumbnail row without a full-screen gallery.

### Episode Detail / Episode Row

Show:

- episode number
- title
- overview
- runtime
- progress
- play / continue action

### Acceptance Criteria

- Given a movie item, when opened, then the Movie Detail Page displays metadata and play actions.
- Given a movie has resume progress, then Continue and Restart actions are both available.
- Given a movie with cast/crew, artwork, and similar movies, then those sections appear in that order below its main details, without season or episode controls.
- Given movie or series recommendations, then Similar loads independently and shows only the same media type; an empty result hides the row, and a local failure offers retry without replacing the main detail content.
- Given a Similar card is opened, then Back returns to the direct source movie or restores the source series with its season and episode selection.
- Given a series item, when opened, then seasons and episodes can be loaded.
- Given missing backdrop, then the detail page still looks usable with fallback background.

## 10.7 Playback

### Goal

The user can play media using the embedded MPV-based player.

### Required Controls

- play
- pause
- stop/exit
- seek
- fast forward
- rewind
- volume
- mute
- fullscreen
- current time
- duration
- buffering state

### Required Shortcuts

The following are defaults; Settings can change or clear player bindings. `Esc` remains reserved for closing a menu, exiting fullscreen, or returning from playback, in that order. See `PLAYER_SPEC.md` section 7 for the complete default set and input exclusions.

| Shortcut | Action |
|---|---|
| Space | play / pause |
| Left | rewind by configured seconds |
| Right | fast forward by configured seconds |
| Up | volume up |
| Down | volume down |
| M | mute / unmute |
| F | fullscreen toggle |
| Esc | exit fullscreen, or exit player if not fullscreen |

### Acceptance Criteria

- Given a playable movie, when the user clicks Play, then video starts.
- Given video is playing, when Space is pressed, then video pauses.
- Given video is paused, when Space is pressed, then video resumes.
- Given video is playing, when Right is pressed, then playback seeks forward.
- Given video is playing, when Left is pressed, then playback seeks backward.
- Given video is playing, when F is pressed, then fullscreen toggles.
- Given playback fails, then the app shows a playback error page/state and does not crash.

The approved quality selector offers Original, 1080p/8 Mbps, 720p/4 Mbps, and 480p/1.5 Mbps for current playback. Capped options depend on server transcoding and are upper limits, not guaranteed output. A switch preserves position and playback state, reports preparation/load failures, and attempts recovery to the previous quality after a replacement-load failure. Playback information distinguishes MPV's actual video resolution/codec from source metadata and never presents source bitrate as live throughput. Detailed rules are in `PLAYER_SPEC.md` sections 9 and 11.

## 10.8 Subtitles

### Goal

The user can select available subtitles during playback.

### Behavior

- Show subtitle list from playback/media stream info.
- Include `关闭字幕` option.
- Display language, title, external/internal marker if available.
- Selecting a subtitle updates MPV.
- Subtitle selection persists for current item playback session.
- Default subtitle language is configurable.
- Timing, size, and position adjustments apply only to the current playback, with reset to defaults. They survive a quality reload of that item and reset for a new item; they do not modify saved subtitle-language preferences or server media. Bitmap or styled subtitles may not support size/position changes, and server-burned subtitles cannot be adjusted as a local subtitle track.

### Acceptance Criteria

- Given an item with subtitles, when the user opens subtitle menu, then subtitles are listed.
- Given a subtitle is selected, then it appears on video.
- Given `关闭字幕` is selected, then subtitles disappear.
- Given no subtitles exist, then the subtitle menu shows `无可用字幕` and is disabled or non-actionable.

## 10.9 Audio Tracks

### Goal

The user can select available audio tracks during playback.

### Behavior

- Show audio track list.
- Display language, codec, channels if available.
- Selecting an audio track updates MPV.
- Default audio language is configurable.

### Acceptance Criteria

- Given an item with multiple audio tracks, when the user opens audio menu, then tracks are listed.
- Given a track is selected, then playback switches to that track.
- Given only one audio track exists, then the menu still shows the current track or is safely disabled.

## 10.10 Playback Progress Sync

### Goal

Playback progress is synchronized to Emby Server so Continue Watching works.

### Behavior

- Report playback start when playback begins.
- Report progress every 5 to 10 seconds while playing.
- Report pause/resume state.
- Report stopped position when exiting player.
- Report completion when playback crosses completion threshold.
- Do not report every UI frame.
- If progress reporting fails, keep playback running and retry later if safe.

### Default Completion Rule

Mark as completed if:

- playback reaches at least 90% of runtime, or
- remaining time is under 2 minutes for long-form video.

This rule can be adjusted after real testing.

For this client, the remaining-time rule applies to `Movie` and `Episode` items whose runtime is greater than two minutes; other or unknown types and clips of two minutes or less use only the percentage rule. This is a product classification, not a general duration standard for long-form video.

The threshold determines the local watched state, not the end of playback. Crossing it sends a real-position progress update while playback and periodic reporting continue. A paused or seeked final position uses the same rule, including seeking backward below the threshold. Emby receives the actual position and runtime and applies its own playstate policy; a successful progress request alone does not confirm that the server marked the item watched. Verify server watched/continue-watching behavior during real-server acceptance.

### Acceptance Criteria

- Given video is playing, when 10 seconds pass, then progress is reported once.
- Given user exits playback at 30 minutes, then stopped position is reported.
- Given user reopens the same item, then Continue starts near the saved position.
- Given progress report fails, then playback does not stop.

## 10.11 Favorites and Played State, P1

### Goal

The user can favorite media and mark items played/unplayed.

### Behavior

- Favorite button on detail page.
- Favorite state on cards where available.
- Mark played/unplayed on detail page or context menu.
- Update UI optimistically only if rollback is handled.

### Acceptance Criteria

- Given an unfavorited item, when user clicks Favorite, then item becomes favorited after server success.
- Given a favorited item, when user clicks Favorite again, then item becomes unfavorited.
- Given user marks an item played, then played indicator updates.
- Given API call fails, then UI shows error and does not lie about final state.

## 10.12 Settings

### Goal

The user can configure basic playback and account behavior.

### Required Settings

- current server display
- current user display
- logout
- switch server
- default subtitle language
- default audio language
- seek step seconds
- auto-play next episode toggle
- player shortcut recording with Ctrl/Shift/Alt combinations, conflict feedback, per-action clearing, and restore defaults; changes use the existing Save/discard settings flow
- actual image-memory and search-index disk usage; separate clearing and current-account index rebuilding
- log export

### Acceptance Criteria

- Given user changes seek step, then keyboard seek uses the new value.
- Given user changes default subtitle language, then future playback prefers that language when available.
- Given user logs out, then token is cleared and Login Page opens.
- Given Settings has unsaved changes, logout or switch-server confirmation states that those changes will be discarded; cancellation keeps both the session and draft unchanged.
- Cache actions require confirmation, show asynchronous progress and recoverable errors, and preserve credentials and preferences. Clearing the search index removes its memory state and owned disk files; rebuilding clears local indexes and reloads the current account. Leaving Settings cancels maintenance and rejects late results.

## 10.13 Local Storage

### Store Locally

- server URL
- current user ID
- current display name
- encrypted access token
- app settings
- recent server list, P1
- search-index files scoped by server and user
- image download bytes in memory only; no persistent image-cache files are written
- Watch Later item metadata in separate persistent files scoped by normalized server and user; no access tokens or playback URLs
- custom player shortcut bindings as general playback preferences

### Do Not Store

- user password
- unencrypted token
- full permanent media library clone
- real media stream URLs longer than needed

## 10.14 Logging

### Required Logs

- app startup
- server connection failures
- login failures, without password
- API non-2xx responses
- playback start/stop/failure
- progress reporting failures
- subtitle/audio switch failures

### Requirements

- Logs must not contain passwords or raw access tokens.
- Logs should include enough technical detail for debugging.
- The app should support exporting logs from Settings.

## 10.15 Session Playback Queue, P1

- Home opens the queue page; the Player opens the same queue inside a popup without leaving playback. Movie and episode details can add playable items. A Series adds the selected episode, or its resolved playable episode when none is selected; an unresolved Series cannot enter the queue.
- Keep current and pending items separate. Adding deduplicates against both; pending items can be played explicitly, removed, moved up/down or to any position, and cleared without removing the current item.
- The queue exists only in memory for the current server/user session. Reusing the same normalized server/user preserves it; logout or a different account/server discards it. App restart starts empty.
- Queue automatic continuation defaults to on for each new session queue. At successful natural EOF, a nonempty pending queue takes priority over automatic next episode. Turning queue continuation off leaves pending items untouched and prevents falling through to automatic next episode. With no pending items, the existing auto-play-next-episode preference applies; its default remains on. There is no inferred next movie outside an explicit queue.
- Peek at the next candidate without consuming it. Commit current/pending changes only after native playback is ready. Preparation failure retains the current playback and candidate; replacement-load failure shows a recoverable error and keeps the candidate rather than skipping it.
- Disable queue playback/editing during preparation and player-initiated transitions. Leaving the queue or closing its popup cancels preparation initiated there; account/navigation changes invalidate late completions. Each actual replacement retains independent playback-instance reporting and cleanup.

## 10.16 Local Watch Later, P1

- Save or remove a Movie, Series, or Episode from its detail page; Home opens the current account's Watch Later poster list. A saved Series remains a Series entry. Opening an entry goes to details and does not start playback or consume the entry.
- Store a deduplicated, most-recently-added-first list on this device, scoped by normalized server and user. Preserve it across app restarts, logout, and server/account switches; show only the active account's list, clearing old visible state on a session change.
- Watch Later is independent of server favorites, watched status, and the session playback queue. Adding/removing entries does not change those states or trigger server library maintenance. Cache clearing does not erase the persistent list.
- Reflect a save or removal only after local storage succeeds; failures retain truthful state and offer retry. Restore a valid backup when available. If both copies are damaged, show a recoverable error and require explicit confirmation to reset only that account's list while retaining damaged files.
- Cancel abandoned operations and reject late results after navigation or session changes. Never persist credentials or media stream URLs in list metadata.

## 11. Non-Functional Requirements

## 11.1 Performance

- Home Page should become usable within 3 seconds on a normal local network.
- Large lists must use pagination or virtualization.
- Images must lazy-load.
- UI thread must not be blocked by HTTP, disk, or MPV IPC.
- Playback page should open without freezing the app shell.

## 11.2 Reliability

- No crash on offline server.
- No crash on empty libraries.
- No crash on missing images.
- No crash on missing subtitles/audio tracks.
- No crash on playback URL failure.
- Token expiry must be recoverable.

## 11.3 Security

- Do not store password.
- Do not store token in plain text.
- Do not log token.
- Do not include personal server URLs in committed tests.
- Support HTTPS server URLs.

## 11.4 Accessibility and Keyboard

- Player shortcuts must work when player is focused.
- Buttons must have visible focus state.
- Text contrast must be readable on dark background.
- Critical actions must not rely only on hover.

## 12. Release Checklist

1. Fresh install test passes.
2. Login and auto-login pass.
3. Movie playback pass.
4. Episode playback pass.
5. Subtitle switching pass.
6. Audio switching pass.
7. Progress sync pass.
8. Offline server error pass.
9. Expired token pass.
10. Empty library pass.
11. Build passes.
12. Tests pass.
13. Logs export works.
14. No password/token leaked in logs or config.

## 13. Open Decisions

These can be decided during implementation, but must be documented once chosen:

- exact MPV integration method
- exact secure token storage implementation
- exact WPF MVVM helper approach
- recent server list is P1, following the current project roadmap
- whether a saved default quality is needed beyond the approved per-playback Original / 1080p / 720p / 480p selector
