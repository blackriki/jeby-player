# Jeby Player 1.0 UI Specification

## 1. UI Direction

The app should feel like a native Windows media player inspired by Emby Web.

Keywords:

- dark
- cinematic
- poster-first
- calm
- readable
- fast
- predictable
- desktop-friendly

The UI should not feel like:

- a database admin tool
- a raw Windows Forms utility
- a browser page pasted into a window
- an e-commerce grid
- a file manager

## 2. Visual Principles

Every new feature must include a considered entry location, visual hierarchy, related-action grouping, empty/error feedback, and keyboard and minimum-window usability. Reuse the existing visual language and review the resulting screen as a whole; adding a functional button alone is not sufficient.

Movie and Series detail actions occupy a single horizontal row. Keep text on the primary playback and Add to Queue actions; use consistent 48 DIP icon buttons for Restart, Favorite, Watch Later and Watched, with descriptive tooltips, accessible names and visible state changes. Verify the whole row fits the detail column at the minimum window size.

### 2.1 Dark First

1.0 uses dark theme only.

Default surfaces:

- app background: near black / deep gray
- cards: slightly lighter dark gray
- overlays: translucent black
- text primary: near white
- text secondary: muted gray
- accent: Emby-style green
- error: clear red tone
- warning: amber tone

Exact values may be implemented as theme tokens. Do not hardcode scattered colors in page files.

### 2.2 Media First

The media poster or backdrop should be the visual anchor.

- Home and library pages prioritize poster cards.
- Detail pages use backdrop image with gradient overlay.
- Player page hides chrome while playing.

### 2.3 Low Noise

- Avoid heavy borders.
- Avoid too many button colors.
- Avoid permanent toolbars covering content.
- Use whitespace and hierarchy instead of boxes everywhere.

## 3. Window Layout

### 3.1 Default Window

- Minimum size: 1100 x 700.
- Default size: around 1280 x 800 or maximize-friendly.
- The app must be usable when maximized.
- Do not require fullscreen for browsing.

Window-state rules:

- A restored window is resizable and uses the normal desktop work area.
- A windowed maximized window fills the current monitor work area. The Windows taskbar remains available and must not cover player controls.
- Explicit player fullscreen, entered with the fullscreen control, `F`, or video double-click, fills the complete current monitor and may cover the taskbar.
- Changing between browsing and Player chrome while already maximized must preserve the maximized state and recalculate the non-client frame without a visible restore/maximize transition.

### 3.2 App Shell

The app shell contains:

```text
┌──────────────────────────────────────────────┐
│ Top Bar                                      │
├──────────┬───────────────────────────────────┤
│ Nav      │ Main Content                      │
│ optional │                                   │
└──────────┴───────────────────────────────────┘
```

Recommended 1.0 layout:

- Top bar with app name, main navigation, search, user/settings.
- Main content scroll area.
- Avoid permanent left nav if it wastes space; top navigation is acceptable.

### 3.3 Top Bar

Required elements:

- App name or logo.
- Navigation: 首页 / 电影 / 电视剧 / 收藏, if enabled.
- Search entry.
- Current user avatar/name.
- Settings button.

Behavior:

- Top bar stays visible on browsing pages.
- Top bar is hidden on player page unless overlay controls are visible.
- Active navigation item is highlighted with accent color or underline.

## 4. Page Inventory

1. Splash / startup state.
2. Server Connection Page.
3. Login Page.
4. Home Page.
5. Library Page.
6. Search Page.
7. Movie Detail Page.
8. Series Detail Page.
9. Player Page.
10. Settings Page.
11. Error fallback page/state.
12. Person Page, reached from movie/series cast and crew.
13. Playback Queue Page, also embedded in the Player queue popup.
14. Watch Later Page, showing the active account's local saved list.

## 5. Page Specifications

## 5.1 Splash / Startup State

### Purpose

Check saved server/token and decide initial navigation.

### UI

- Centered app name.
- Small loading indicator.
- Optional status text: `正在启动...`.

### Behavior

- If no server, go to Server Connection Page.
- If server exists but no token, go to Login Page.
- If server and token exist, validate token then go Home.
- If validation fails with 401, go Login.
- If server unreachable, show reconnect state with `重试` and `切换服务器`.

## 5.2 Server Connection Page

### Layout

```text
[App Name]
连接到你的 Emby Server
[ Server URL input                         ]
[连接]
[error text]
```

### Details

- Centered card layout.
- Input label: `服务器地址`.
- Placeholder: `例如 http://192.168.1.100:8096`.
- Button text: `连接`.
- Loading state: button shows `正在连接...` and disables input.
- Below the connection controls, show `最近服务器` with up to five addresses and a remove action per row. Selecting an address only fills the input; removal affects the history list only.
- Show loading, empty, and retryable history-error states separately from connection errors. Preserve a switch-server partial-cleanup warning when a history action fails.
- Keep the card centered when space permits and allow vertical scrolling on smaller windows. Long addresses are truncated visually and available in a tooltip; buttons expose hover, keyboard focus, and disabled states.

### States

| State | UI |
|---|---|
| Empty | Connect disabled |
| Invalid URL | Inline validation below input |
| Connecting | Loading indicator, controls disabled |
| Failed | Error text + retry allowed |
| Success | Navigate to Login |

## 5.3 Login Page

### Layout

```text
[Server name or URL]
登录 Emby
[ Username ]
[ Password ]
[登录]
[切换服务器]
[error text]
```

### Details

- Password input must mask text.
- Do not add “remember password”.
- Login button disabled when username is empty.
- Pressing Enter in password field submits login.
- `切换服务器` is a secondary action with a visible keyboard focus state. It does not ask for a second confirmation on Login, prevents duplicate submission, clears authentication and the saved server, then opens Server Connection Page.
- Re-entering Login clears stale credentials and errors before loading the saved server. Asynchronous saved-address loading must not overwrite newer user input, and only the latest concurrent load may apply.
- A switch that fails before authentication cleanup stays on Login and shows a friendly error. If authentication and runtime cleanup succeed but the saved address cannot be cleared, Server Connection opens with that address still editable and a clear warning.
- If secure-token deletion succeeds but local account-metadata cleanup fails, the runtime session is no longer trusted: clear it and navigate to Login or Server Connection with an explicit partial-cleanup warning.

### Error Text

- Wrong username/password: `用户名或密码错误`.
- Offline server: `服务器不可用，请检查地址或网络`.
- Timeout: `登录超时，请稍后重试`.

## 5.4 Home Page

### Layout

```text
Top Bar

继续观看
[poster] [poster] [poster] ...

最近添加
[poster] [poster] [poster] ...

电影
[Library card] [poster preview] ...

电视剧
[Library card] [poster preview] ...
```

### Section Rules

- Continue Watching, Recently Added, and every media category row use one consistent `查看全部` button aligned to the right of the title. The borderless text-and-chevron action has visible hover, keyboard focus, pressed, and disabled states and a section-specific accessible name; it must never be decorative text.
- `查看全部` opens a dedicated list titled for the selected section. Reuse the shared poster cards, authenticated-image placeholders, and watched/progress treatments in a wrapping grid. Provide return-to-Home, refresh, load-more, and loading/empty/error/retry states. Keep the Home row's query and grouping semantics while loading beyond its 24-card preview.
- Returning from a detail page restores the selected full section and all loaded pages. Leaving the list cancels pending requests; switching sections or accounts must not display stale cards.
- Horizontal rows should scroll smoothly.
- Mouse wheel over row may scroll vertical page unless row is focused/hovered; keep behavior predictable.
- Do not load every image at once.

### Card Rules

Home cards use poster ratio around 2:3.

Each card shows:

- poster
- title
- year if available
- progress bar if partially watched
- watched badge if watched
- favorite badge if favorite, P1

Hover:

- slight scale up
- dark overlay
- quick play icon
- title remains readable

Do not use extreme zoom or animated flipping.

## 5.5 Library Page

### Movie Library Layout

```text
Top Bar

[Library Title]
[Sort dropdown] [Filter button] [View options]

Poster Grid
[poster] [poster] [poster]
[poster] [poster] [poster]
```

### TV Library Layout

Same as movie library, but cards open Series Detail Page.

### Sorting and Filtering, P1

- Align a compact toolbar to the right of the selected library title. The sort button shows the current field and direction, such as `名称 ↑`; its native dark menu groups `排序依据` (名称/添加时间/年份) and `排序顺序` (升序/降序), with a check beside each current choice.
- A separate icon-and-label `筛选` button opens grouped single-choice `观看状态` (全部/未观看/已观看) and `收藏` (全部/仅收藏) menus. Active filters show a numeric count, and the accessible name/tooltip describes the conditions. Show `重置` only when sorting or filters differ from defaults. Refresh is a lightweight icon button with an accessible name and tooltip.
- Sort, filter, reset, and refresh use transparent, borderless buttons without rounded boxes. Text and icons turn green only while hovered or while their menu is open; retained input focus and active filter conditions must not leave them green after dismissal. Keyboard navigation uses an underline focus cue, and pressed/disabled states remain visible. Reuse existing dark surface, text, green-accent and button resources; the locally styled native menus use restrained rounded corners and dividers. Keep stable automation IDs. Tab follows visual toolbar order; Enter/Space or Down opens a menu, arrow keys navigate choices, and Esc or an outside click closes it. Esc returns focus to its trigger; clicking another control keeps focus at that control. Menu rows use checkmarks and a subtle hover/focus background without a selection outline. The toolbar stays compact at the minimum desktop size; long library titles ellipsize before actions are clipped.
- Changing an option reloads the first page and clears results belonging to the previous query. Keep query controls available while item results load so another selection can cancel the current request. Library-list loading disables them until a library is available.
- Switching between sort and filter waits for the previous native menu to finish closing, then opens the requested menu once. A delayed close must not steal focus from the new menu or reopen a menu after leaving the page.
- A filtered empty result says `没有符合筛选条件的内容` and suggests adjusting the watch/favorite conditions. An unfiltered empty library keeps its existing empty message.
- Refresh preserves existing cards during loading and on failure only when the library and query are unchanged. A visible refresh action supports retry. Reset restores name ascending and all items in one request.
- Leaving Library cancels its work. Returning from Detail in the same session preserves query choices; authentication navigation clears them.

### Behavior

- Use grid layout with adaptive columns.
- Use virtualization or incremental loading.
- Show loading skeleton while fetching.
- Show empty state when no items.
- Show retry button when fetch fails.

### Empty State

Text: `这个媒体库暂时没有内容`.

### Error State

Text: `媒体库加载失败`.

Actions:

- `重试`
- `返回首页`

## 5.6 Search Page

### Layout

```text
[Search input                              ]

搜索结果
电影
[poster] [poster]

电视剧
[poster] [poster]

剧集
[row] [row]
```

### Behavior

- Search input receives focus when page opens.
- Debounce typing.
- Show loading after debounce starts.
- Do not show stale results after a newer search completes.
- Empty keyword state: `输入关键词搜索你的媒体库`.
- No result state: `没有找到相关内容`.

## 5.7 Movie Detail Page

### Layout

```text
Movie backdrop background with dark gradient

[Poster]   Title
           Year · Runtime · Rating
           Genres
           Overview

           [播放] [继续播放] [从头播放] [收藏] [已观看]

Cast / crew:
[portrait] [portrait] [portrait] ...
Name / role

Artwork:
[wide thumbnail] [wide thumbnail] [wide thumbnail] ...

Similar movies:
[poster card] [poster card] [poster card] ...
Title / year / watched or progress
```

### Visual Rules

- Backdrop must have a gradient overlay from dark to transparent.
- Text must remain readable even on bright backdrops.
- Poster should have rounded corners and soft shadow.
- Primary action is `播放` or `继续播放` depending on progress.
- Below the main details, show cast/crew, artwork, then Similar using the same cards, horizontal paging, placeholders, and empty-section behavior as Series detail. Movie detail does not show season/episode controls or reserve space for them.
- Cast/crew and artwork come from the movie's existing detail response, with the same 24-person and 12-artwork limits as Series detail.
- Similar loads up to 12 movies independently, with local loading/error/retry state; an empty result hides the row.

### Action Rules

- If no resume progress: show `播放`.
- If resume progress exists: show `继续播放` and `从头播放`.
- Favorite and played actions are secondary.
- Opening a Similar movie navigates to its normal detail page. Back returns to the direct source movie; each further Similar navigation replaces this one-level source.

## 5.8 Series Detail Page

### Layout

```text
Backdrop background with dark gradient

[Poster]   Series Title
           Year range · Rating
           Overview

Season selector:
[第 1 季] [第 2 季] ...

Quick episode selector:
[1] [2] [3] [4] [5] ...

Horizontal episode cards:
[16:9 image] [16:9 image] [16:9 image] ...
S01E01 Title  S01E02 Title  S01E03 Title
Overview      Overview      Overview
[progress / watched state]

Cast and crew:
[portrait] [portrait] [portrait] ...
Name       Name       Name
Role       Role       Role

Artwork:
[wide thumbnail] [wide thumbnail] [wide thumbnail] ...

Similar:
[poster card] [poster card] [poster card] ...
Title / year / watched or progress
```

### Behavior

- Default selected season should be the next unwatched season if easy to determine; otherwise first season.
- Within a season, the initial selected episode is: requested episode, then partial-progress episode, then first unwatched episode, then first episode.
- On Series detail, the selected episode's Primary image is layered over the series backdrop as the Hero. While episodes load, during a season change, or when the selected episode has no usable image, the series backdrop remains visible underneath. This first version does not cross-fade, add requests, or replace the series-level title, overview, poster, and primary playback recommendation.
- The quick-number track and card track share the same selected episode. Clicking a quick number only selects it. Clicking an episode card selects that episode and immediately starts its play/continue action; neither interaction navigates to a separate episode detail page.
- The series-level primary action remains a recommendation derived from resume/watch history and is independent of quick-number selection. A whole-card episode activation always plays that card's episode, while Enter plays the selected episode.
- Episode cards are approximately 280-300 DIP wide with a 16:9 image, a compact horizontal episode-number/runtime line, title (maximum two lines), overview (maximum three lines), partial progress, and the shared watched badge. They do not add a second bottom action row. Selected and watched remain independent states.
- Episode and Similar cards use the interactive Home card language: a 10 DIP clipped radius, transparent default border, subtle hover/focus/pressed treatment, and shared watched/progress resources where applicable. Episode pointer hover fades in the shared centered play affordance over the image in 140 ms; keyboard focus shows it immediately. This affordance is visual-only and the whole episode card remains the single action target.
- Person and artwork cards reuse the same 10 DIP surface, transparent border, clipping, and subtle pointer hover. Person cards are focusable buttons that open the person page; artwork remains read-only, stays out of the Tab sequence, and exposes no Invoke or pressed action. Keyboard paging remains available on the row ScrollViewer and arrow buttons.
- The quick-number and episode-card tracks scroll horizontally with recycling virtualization. Their selected episode is centered immediately after initial load, player return, selection changes, and window resizing; centering cancels any active paging animation first and does not change panel margin/padding or force synchronous layout.
- Season, quick-number, episode-card, cast/crew, artwork, and Similar rows hide their horizontal scroll bars and use overlaid 42 DIP previous/next buttons. Both buttons collapse when there is no overflow; the previous button collapses at the left edge, the next button collapses at the right edge, and available buttons appear only while the row is hovered or contains keyboard focus.
- Row arrow and keyboard paging moves 95% of the visible viewport over 220 ms with cubic ease-out, continues from the current animated position on repeated input, and jumps directly when Windows client-area animation is disabled. Plain wheel input remains vertical; Shift + wheel is horizontal.
- The outer detail page scrolls to the episode section only when navigation supplies a focused episode (including player return). A normal series open remains at the top of the page; inner-track centering never moves the outer viewer.
- On the episode tracks, Left/Right changes selection and Enter plays. Cast/crew, artwork, Similar, and season rows can page from the keyboard without showing a scrollbar.
- Episode controls expose visible keyboard focus and accessible names containing episode number, title, and watched state.
- Any supported independent episode-detail route stores both season and episode in its detail back target so returning restores the exact series selection.
- Sections appear in the order episodes, cast/crew, artwork, then Similar. Cast/crew and artwork are series-level metadata from the existing detail load, so selecting an episode does not reload either section.
- Cast/crew is a lightweight horizontal row in server order, limited to 24 entries. Each card contains the portrait, name, and one role line without repeating the person type. Missing portraits use a stable placeholder; an empty list hides the entire section. Cards are keyboard-focusable buttons with hover/focus states and open the person by server ID.
- Artwork is a lightweight horizontal row of consistently clipped, sized backdrop/Art thumbnails, de-duplicated and limited to 12. Failed images use a placeholder; an empty list hides the entire section. Each thumbnail has a distinct automation name, but selection and full-screen preview are out of scope.
- Similar on Series detail is a horizontal row of at most 12 series poster cards using the Home card language, with watched/progress state, stable missing-image fallback, visible focus/hover, and previous/next buttons. Movie detail reuses this row for movies. A successful empty result hides the whole row; loading or failure remains local to the row and failure offers retry without replacing episodes, cast/crew, or artwork.
- Opening a Similar card navigates to its normal detail page. Back restores the directly preceding source Series, selected season, and selected episode; opening another Similar item replaces that one-level source rather than building an unlimited detail stack.
- `继续观看下一集` shortcut is P1.

### Person Page

Show the person's portrait, name, biography, and a wrapping poster grid of Movie/Series works, loaded in pages of 48. Use a placeholder portrait and `暂无人物简介` when needed. Profile loading/error/retry and works loading/empty/error/retry/load-more states remain distinct. A failed later works page retains existing cards. Back from a work restores the person page's items and scroll position; Back from the person returns to the originating movie or selected series/season/episode. Leaving the page cancels requests, and account changes discard prior person data.

## 5.9 Player Page

### Layout

The player is full-window, not just a small embedded panel.
Full-window playback does not implicitly enter fullscreen: a maximized app remains constrained to the current monitor work area until the user explicitly toggles fullscreen.

```text
Video Area

Top overlay, visible on mouse move:
[Back] [Title] [Minimize] [Close]

Bottom overlay:
[Play/Pause] [time] [progress bar] [duration] [volume] [subtitle] [audio] [quality] [fullscreen]
```

### Control Overlay Behavior

- Playback windows can shrink to 640×360 DIP; browsing keeps its 1100×700 DIP minimum. Returning from playback restores the previous normal browsing bounds after fullscreen teardown, without changing a maximized window's state.
- Below 900 DIP wide, keep play/pause, next episode when available, progress, volume, More and fullscreen directly accessible. Move subtitles, audio, quality, speed, queue and playback information into More; retain their existing commands and mutually exclusive menus. The title trims and the optional media logo collapses.
- Below 500 DIP high, reduce chrome insets and gradient coverage. Menus stay inside the player width and use scrolling for long contents; all More entries fit at the minimum size. Resizing closes open menus and restores full controls when space is available.

- Place the current speed (for example `1.5×`) in the bottom action row beside quality. Its popup uses the same spacing, focus treatment and mutual exclusion as other player menus; Escape closes it before leaving fullscreen.
- A held-frame speed override uses a fixed top-center `2× 播放中 · 松开恢复` hint independent of control-bar visibility. Failures use the same area to show actual speed and a retry action, without moving controls or skip/next targets.
- Settings groups the default subtitle switch with audio/subtitle language preferences, explaining that same-series manual choices take precedence. Keep save/cancel and failed-write feedback consistent with existing preferences.

- Show overlay when player opens.
- Show overlay on mouse move.
- Hide overlay after 3 seconds of mouse inactivity while playing.
- Keep overlay visible while paused.
- Keep overlay visible while menus are open.
- Hide cursor together with controls while playing and inactive.
- Skip-intro/credits actions and the next-episode prompt stay anchored 28 DIP from the right and 132 DIP above the player bottom, matching the controls-visible position. Showing or hiding controls must not move these click targets.
- Minimize delegates to the owning app window and preserves playback, the current Player page, and the windowed-maximized or explicit-fullscreen restore state. Restore returns to the same playback session.
- Minimize and Close use the same player-overlay icon-button size and interaction states; Minimize appears immediately before Close.

### Progress Bar Behavior

- Hover shows preview time if implemented; P1.
- Dragging seeks only after user releases or throttles seek events.
- Pressing the progress track moves the thumb to that position immediately and continues smoothly when the pointer moves without being released.
- Do not spam Emby progress endpoint while dragging.

### Menus

Subtitle menu:

```text
字幕
✓ 中文 简体 SRT
  English ASS
  关闭字幕
```

Audio menu:

```text
音轨
✓ Japanese · AAC · 2.0
  Chinese · AC3 · 5.1
```

Volume track presses move the volume preview immediately and capture the pointer so the same press can continue as a smooth drag. The volume popup must not compete for that pointer capture, and clicks anywhere inside its presented content must remain internal even when WPF routes them through the logical overlay parent. Pressing another Player control closes the popup without consuming the same press, so the progress track can begin dragging on its first attempt. Switching activation between the owner and Player overlay does not close it, while deactivating the whole application does. Direct thumb dragging keeps the same behavior. Both paths commit once through the existing Player volume pipeline on release; capture loss restores the pre-drag value without committing. The volume icon shows the muted state when mute is enabled or volume reaches zero.

Quality menu:

```text
画质
✓ 原画
  1080p · 8 Mbps
  720p · 4 Mbps
  480p · 1.5 Mbps
```

These are resolution/bitrate ceilings, not promises of the resulting stream. Non-original choices require server transcoding. Show switching progress, disable conflicting actions, preserve playback position and state, and give a recoverable message if preparation fails. The selector applies to current playback and is not a saved Settings default.

The subtitle popup includes timing ±0.1 seconds (−60 to +60), size ±10% (50–300%), position ±5% (0–100%, with 100% at the bottom), and reset. Explain that adjustments are temporary, positive timing delays subtitles, and bitmap/styled subtitles may ignore size or position. Burned-in server subtitles are part of the video and cannot be locally adjusted.

The playback-information popup separates MPV's actual video resolution/codec from server-provided source resolution/codec/total bitrate and the negotiated playback method. It supports refresh and shows unavailable fields honestly; source bitrate must not be labeled live network throughput or actual transcode bitrate.

The queue popup embeds the Playback Queue page without its Back button and participates in the same mutually exclusive popup behavior. Opening it keeps the Player page and native playback alive. `下一待播` selects the first pending item; queue play/edit controls are disabled during a playback transition. Closing the popup cancels preparation initiated from its list.

### Player Error State

The player header includes icon-only Mini and Pin actions with tooltips and accessible names. Mini is an explicit 480×270 DIP mode with a restore action, compact title, transport controls and More. Pin has a visible active color and is disabled in fullscreen. Popup and next-episode positions must fit the mini window, and caption dragging must not cover video below the compact header.

Seek hover previews are non-interactive, positioned just above the progress bar and clamped within the player. Show target time and available cached frames immediately while moving, then refine with a server thumbnail or an independently decoded local frame. Retain the displayed image during loading. Show only the pointer target time below the image, with no second frame timestamp or reserved blank row; cached images are approximate previews. Mini mode uses a smaller image. Unsupported sources without images stay time-only. Ignore obsolete results and hide previews when leaving the bar, opening another context, resizing or leaving playback.

The subtitle menu includes “载入本地字幕…” with a file picker for SRT/ASS/SSA/VTT; dropping one supported file onto the player uses the same behavior. Show the imported track as 本地, display only its filename, and keep import feedback within the subtitle menu. Do not add another permanent transport button.

The playback-error card is rendered in the native player control overlay so it remains clickable above the video surface. Provide “重试” and return actions with a reconnecting state; disable duplicate retries and retain the user's position/settings across attempts.

If playback fails:

```text
播放失败
无法播放这个媒体，请稍后重试或检查服务器转码设置。
[重试] [返回]
```

If available, include a small expandable technical detail section.

## 5.10 Settings Page

### Layout

Grouped settings, not a giant form.

Groups:

- Account
- Server
- Playback
- Subtitles
- Network
- Logs
- About

### Required Actions

- logout
- switch server
- export logs
- record, clear, and restore player shortcut bindings, including Ctrl/Shift/Alt combinations
- refresh actual image-memory/search-index disk usage, clear each cache separately, and rebuild the current account's index

The Local Data panel shows `尚未读取` before measurement and real zero values for empty caches. Image download bytes are cached in memory only. Index usage measures owned disk files; clearing also invalidates the in-memory index. Clearing/rebuilding opens an inline confirmation, defaults focus to Cancel, supports Esc, and scrolls confirmation into view in a small window. Busy/error/retry/success states do not edit player preferences. Index rebuilding clears local indexes before rebuilding the current account; other accounts rebuild on demand. Cancel pending maintenance on leaving Settings or confirming an account change, and ignore late session results.

Logout and switch server reuse the page's single confirmation overlay. `取消` receives initial keyboard focus and Esc cancels. When settings are dirty, the same confirmation explains that unsaved changes will be discarded. Cleanup disables repeat submission; success navigates to Login or Server Connection. Authentication cleanup failure keeps the draft and page visible with a friendly error. If only saved-address cleanup fails after the session is cleared, Server Connection opens with the retained address and a non-misleading warning.

Authentication cleanup distinguishes failure before secure-token deletion from failure after it. The former keeps the current page and runtime session for retry; the latter clears runtime state, closes the account-action flow, and routes to a safe authentication page with a warning.

The shortcut panel shows each action and its effective gesture. Activating a row records the next supported key combination; show `请按快捷键…（Esc 取消）` while recording. Conflicts identify the already-bound action and leave the old value intact. Esc or focus leaving the row cancels capture, so typing in Settings search does not change a binding. Each row can be cleared, the complete set can be restored to defaults, and changes remain drafts until the normal Save action succeeds. Keep recording, error, focus, and disabled states visible in a scrollable small-window layout.

An explicit media-library scan may run from Settings. A confirmed account action cancels and invalidates any in-flight scan so late scan results cannot replace the account navigation. Conversely, a current scan response of `401` is a forced authentication transition: it clears the runtime session and opens Login even when settings are dirty or an account confirmation is visible.

## 5.11 Playback Queue Page

Home shows a compact `待播 N 项` entry in its header actions only while pending items exist. The player keeps its bottom-right queue entry, displaying `队列 · N` when populated and `队列` when empty. Counts update immediately and exclude the current item. Detail pages group Add to Queue with Play/Continue. The standalone page has Back to Home, a current-item card, pending row cards, an `自动连播` toggle, and `清空待播`. Each pending row shows a poster placeholder when needed, an ellipsized title, type/year, play action, up/down buttons, a position selector, and remove. The first/last movement limits are visibly disabled. Current playback is separate and survives clearing pending items.

Show an empty explanation directing users to Movie/Episode details, preparation progress, and recoverable playback errors. Details provide `加入队列`; Series details resolve the selected/playable episode and identify that episode in the success message. Repeat additions give truthful already-in-queue feedback. The same layout fits a narrow Player popup with vertical scrolling and fully reachable row controls; embedded mode hides Back.

Queue continuation defaults on. Pending items take precedence over automatic next episode at natural EOF; turning this toggle off prevents both queue continuation and episode fallback while pending items remain. When pending is empty, the saved automatic-next-episode setting applies. Queue contents are session-only and clear on logout or a different account/server.

## 5.12 Watch Later Page

Home places Search, Watch Later, Favorites and Settings in one header row at normal desktop widths, with matching icon buttons, tooltips and accessible labels for Watch Later and Favorites. A Watch Later poster row appears directly below Continue Watching, offers View All, and hides when empty. Preview up to 12 items and refresh on every return to Home independently of the network snapshot cache. Local read failures have inline retry without blocking other Home content. Detail pages group Watch Later next to Favorite; the action saves or removes the displayed Movie/Series/Episode independently from Favorite and Add to Queue. Use clear success/error feedback; a failed local write must not claim the item changed.

The page identifies the list as local to the current server/account. Use poster cards with missing-image placeholders, title/year, open-detail action, and a separate remove action. Provide Back to Home, refresh, loading/empty/error/retry states, and retain the list/scroll when returning from details. Opening a card does not immediately play or remove it. A failed removal keeps the card visible.

For a damaged list with no usable backup, offer retry or an explicit reset confirmation explaining that only the current account's list is reset and damaged copies are retained. Canceling leaves it untouched. Account changes clear visible old content, while the saved list remains available when that account returns; cache actions do not delete it.

## 6. Component Specifications

## 6.1 Media Card

### Variants

- Poster card.
- Wide backdrop card, optional.
- Library card.
- Episode row.

### Poster Card Required Content

- image
- title
- progress bar if progress > 0 and < completed threshold
- watched badge if watched
- missing image placeholder

### Hover State

- overlay appears
- quick play button appears
- card slightly elevates or scales
- cursor changes to pointer

### Focus State

Keyboard focus must be visible.

## 6.2 Buttons

### Types

- Primary: play, connect, login.
- Secondary: restart, retry, settings actions.
- Icon: fullscreen, volume, subtitle, audio.
- Danger: logout, clear data.

### Rules

- Disabled state must be clear.
- Loading button must prevent duplicate submissions.
- Primary buttons should use accent color.

## 6.3 Dialogs

Use dialogs sparingly.

Allowed:

- logout confirmation
- switch server confirmation
- clear cache confirmation
- fatal error details

Logout preserves the selected server and general local preferences. Switch server clears the selected server after authentication is cleared. Both confirmations default focus to the non-destructive cancel action.

Avoid nested dialogs.

## 6.4 Toasts

Use toasts for non-blocking feedback:

- favorite saved
- setting saved
- progress sync temporary failure, optional
- log exported

Do not use toasts for critical errors that require action.

## 7. Motion and Animation

Allowed:

- page fade transition, under 200 ms
- card hover scale, subtle only
- overlay fade in/out
- loading skeleton shimmer or spinner

Not allowed:

- 3D card flip
- long page transitions
- bouncing buttons
- excessive hover animations
- decorative particle effects

## 8. Keyboard and Mouse Interactions

### Global

- Ctrl+F or `/`: focus search, P1.
- Alt+Left: go back, P1.
- Esc: close modal/menu or go back depending context.

### Browsing

- Enter on focused card opens details.
- Space on focused card should not start playback unless explicitly designed.
- Mouse double-click on card may play directly, P1.

### Player

Keyboard entries below are defaults. Settings can replace or clear them; player help reflects saved bindings. Exact modifiers matter. Text/password input and focused popup controls retain their own keyboard behavior. Esc remains reserved for menu close, fullscreen exit, then player return.

| Input | Action |
|---|---|
| Mouse move | show controls |
| Single click video | toggle controls, optional |
| Double-click video | fullscreen toggle |
| Space | play / pause |
| Left | rewind |
| Right | fast forward |
| Up | volume up |
| Down | volume down |
| M | mute |
| F | fullscreen |
| J | next subtitle, including Off |
| Ctrl+Left / Ctrl+Right | subtitle earlier / later by 0.1 seconds |
| A | next audio track |
| Esc | close menu, exit fullscreen, or return from player |

## 9. Page States

Every data-driven page must implement these states:

| State | Requirement |
|---|---|
| Initial | no stale content flash |
| Loading | spinner or skeleton |
| Empty | friendly explanation |
| Error | message + retry where possible |
| Success | content visible |

## 10. Responsive Rules

### Width >= 1400

- More poster columns.
- Wider details content.

### Width 1100-1399

- Default desktop layout.

### Width < 1100

- App remains usable.
- Top navigation may collapse non-critical labels.
- Do not hide core actions.

## 11. UI Do Not Rules

Do not:

- use a white default admin dashboard style
- use raw unstyled WPF controls on final screens
- use data tables for media browsing
- use tiny poster cards that waste the cinematic feeling
- show broken image icons
- block the whole app when only one section fails
- show technical stack traces directly to normal users
- hide critical actions behind hover only
- make player controls permanently cover subtitles
- make seek/volume interactions require pixel-perfect clicking
