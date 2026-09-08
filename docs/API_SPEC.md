# Jeby Player 1.0 API Specification

## 1. Purpose

This document defines how the app should communicate with Emby Server.

All Emby API access must be centralized in `EmbyApiClient`. UI code must not directly call HTTP endpoints.

## 2. API Principles

- Use user authentication for this desktop client.
- Keep the current user context explicit.
- Never store passwords.
- Store access tokens securely.
- Normalize server URLs consistently.
- Handle every non-success response intentionally.
- Return app-friendly domain models to UI layers.
- Do not expose raw HTTP details to view models unless necessary.

## 3. Server URL Normalization

### Input Examples

```text
192.168.1.100:8096
http://192.168.1.100:8096
https://media.example.com
https://media.example.com/emby
```

### Rules

1. Trim whitespace.
2. Require or infer protocol based on UI behavior.
3. Remove trailing slash.
4. Prefer the Emby API base path `/emby`.
5. If the user already entered a URL ending in `/emby`, do not append another `/emby`.
6. Use a single `ServerConnectionInfo` model to store normalized values.

### Example

```text
Raw input:      http://192.168.1.100:8096/
Server base:    http://192.168.1.100:8096
API base:       http://192.168.1.100:8096/emby
```

## 4. Authentication

### Endpoint

```http
POST /Users/AuthenticateByName
```

### Request Body

```json
{
  "Username": "user name",
  "Pw": "plain password from input, never stored"
}
```

### Expected Response Data

The response should include:

- access token
- user object
- user id
- display name

### Storage

Store:

- access token, encrypted/secure storage
- user id
- username/display name
- server URL

Do not store:

- password
- unencrypted token

## 5. Authentication Headers

Authenticated requests must include the access token.

Required:

```http
X-Emby-Token: {AccessToken}
```

Recommended client identification header:

```http
X-Emby-Authorization: MediaBrowser Client="Jeby Player", Device="Windows", DeviceId="{stable-device-id}", Version="1.0.0"
```

The device id must be stable per app install and must not contain personal data.

## 6. Core Client Interface

```csharp
public interface IEmbyApiClient
{
    Task<ServerPublicInfo> GetPublicSystemInfoAsync(CancellationToken cancellationToken);
    Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
    Task<UserProfile> GetCurrentUserAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken cancellationToken);
    Task<PagedResult<MediaItemSummary>> GetItemsAsync(GetItemsRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaItemSummary>> GetLatestItemsAsync(GetLatestItemsRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaItemSummary>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    Task<MediaItemDetail> GetItemDetailAsync(string itemId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SeasonSummary>> GetSeasonsAsync(string seriesId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EpisodeSummary>> GetEpisodesAsync(string seriesId, string seasonId, CancellationToken cancellationToken);
    Task<PlaybackInfo> GetPlaybackInfoAsync(string itemId, PlaybackInfoRequest request, CancellationToken cancellationToken);
    Task ReportPlaybackStartedAsync(PlaybackReport report, CancellationToken cancellationToken);
    Task ReportPlaybackProgressAsync(PlaybackProgressReport report, CancellationToken cancellationToken);
    Task ReportPlaybackStoppedAsync(PlaybackStopReport report, CancellationToken cancellationToken);
    Task SetFavoriteAsync(string itemId, bool isFavorite, CancellationToken cancellationToken);
    Task SetPlayedAsync(string itemId, bool isPlayed, CancellationToken cancellationToken);
}
```

## 7. Endpoint Map

Exact query parameters may be adjusted after testing against a real Emby Server, but all changes must stay inside `EmbyApiClient`.

| Feature | Endpoint |
|---|---|
| public server info | `GET /System/Info/Public` |
| authenticate | `POST /Users/AuthenticateByName` |
| list items | `GET /Items` or `GET /Users/{UserId}/Items` |
| item detail | `GET /Users/{UserId}/Items/{Id}` |
| latest items | `GET /Users/{UserId}/Items/Latest` |
| similar items | `GET /Items/{Id}/Similar` |
| playback info | `POST /Items/{Id}/PlaybackInfo?UserId={UserId}`; compatible Original requests may fall back to GET |
| person profile | `GET /Users/{UserId}/Items/{PersonId}` |
| person's works | `GET /Users/{UserId}/Items?PersonIds={PersonId}&IncludeItemTypes=Movie,Series` |
| playback progress | `POST /Sessions/Playing/Progress` |
| mark favorite | `POST /Users/{UserId}/FavoriteItems/{Id}` |
| unmark favorite | `DELETE /Users/{UserId}/FavoriteItems/{Id}` or documented delete equivalent |
| mark played | `POST /Users/{UserId}/PlayedItems/{Id}` |
| image | `GET /Items/{Id}/Images/{Type}` |

Home full-section lists use the existing Home feature service and `MediaCard` mapping, including resume position and episode parent context. `LoadSectionAsync` takes the section kind, start index, and page limit and returns cards, the next source index, and whether more records remain. Continue Watching uses the Home resumable-video filter and `DatePlayed` descending; movies, series, and box sets use the matching type and `DateCreated` descending. Requests remain authenticated, cancellable, bounded, and eligible for the existing route-prefix fallback.

Recently Added retains `/Users/{UserId}/Items/Latest` and its default grouping, adding `StartIndex` and `Limit`; the [Emby Latest Items guide](https://dev.emby.media/doc/restapi/Latest-Items.html) documents pagination and grouping. Its array response has no total, so end-of-list detection uses the source page length. Advance pagination by consumed source rows before UI deduplication.

Animation full lists query every library matched by the existing animation classifier. Each library contributes a bounded prefix sufficient for the requested global page, loaded in server pages; results are merged by `DateCreated`, deduplicated, and then paged globally. The global offset must not be applied separately to every library. An absent animation library produces a successful empty list.

The existing item-detail request maps `People`, `BackdropImageTags`, and `ImageTags.Art` from the same response. It must not add a `Fields` query solely for the movie/series-detail cast/artwork sections, and it must not call an item-images listing endpoint. Person image and artwork URLs use sized `GET /Items/{Id}/Images/{Type}` variants and never include the access token in the URL.

The existing series episodes request explicitly includes `ImageTags` in `Fields`. When an episode has a Primary image tag, the same response maps a 480-pixel card thumbnail and a 1600-pixel, quality-90 Hero image URL. Both URLs include the URL-encoded image tag and never include the access token; a missing Primary tag leaves both URLs empty and does not cause another request.

Movie and Series detail load Similar independently through their feature service. The request uses `UserId`, `Limit=12`, `EnableImages=true`, `ImageTypeLimit=1`, `EnableImageTypes=Primary`, `EnableUserData=true`, and `IncludeItemTypes` set to the current detail type (`Movie` or `Series`). The service excludes the current item and other media types, preserves server order, and maps id, title, type, year, played state, resume progress, and a sized Primary poster URL without token query data. Other detail types do not request Similar. Missing or empty `Items` is a successful empty result. The feature service may retry once with the `/emby` API prefix after a route-level `404` or `405`; final `404`, `401`, `403`, timeout, cancellation, invalid JSON, and `5xx` remain distinct results.

`IPersonService` / `EmbyPersonService` load the selected server person ID, biography, and portrait, then page that person's Movie/Series works in the current user context. Works requests use `Recursive=true`, `StartIndex`, `Limit`, `SortBy=ProductionYear,SortName`, `SortOrder=Descending`, user data, and sized Primary images. The UI requests 48 rows, advances by consumed source rows before deduplication, and stops on the reported total or a terminal page. Profile and works errors remain independently recoverable; requests carry authentication, a five-second timeout, cancellation, and the existing one-time `/emby` fallback after route-level 404/405.

## 8. Request Models

### GetItemsRequest

```csharp
public sealed class GetItemsRequest
{
    public string? ParentId { get; init; }
    public string? SearchTerm { get; init; }
    public IReadOnlyList<string> IncludeItemTypes { get; init; } = Array.Empty<string>();
    public string? SortBy { get; init; }
    public string? SortOrder { get; init; }
    public int StartIndex { get; init; }
    public int Limit { get; init; } = 50;
    public bool Recursive { get; init; } = true;
    public bool? IsPlayed { get; init; }
    public bool? IsFavorite { get; init; }
}
```

### PlaybackInfoRequest

```csharp
public sealed class PlaybackInfoRequest
{
    public string UserId { get; init; } = string.Empty;
    public long? StartTimeTicks { get; init; }
    public int? MaxStreamingBitrate { get; init; }
    public string? DeviceProfileId { get; init; }
    public bool AllowVideoStreamCopy { get; init; } = true;
    public bool AllowAudioStreamCopy { get; init; } = true;
}
```

The quality request carries start ticks, the chosen media source, selected audio/subtitle stream indices, and (for capped quality) `MaxStreamingBitrate` plus a device profile. Original applies no new quality cap and retains normal compatibility selection. 1080p/720p/480p request ceilings of 1920×1080 at 8 Mbps, 1280×720 at 4 Mbps, and 854×480 at 1.5 Mbps. Capped requests disable DirectPlay/DirectStream and video stream copy, and request the server-provided HLS H.264/AAC transcode URL. They must not fall back to a GET request that loses the required device profile, fabricate a stream URL, or silently play an uncapped original source. Unsupported transcoding or permissions are recoverable failures; actual output depends on the server and source.

### PlaybackProgressReport

```csharp
public sealed class PlaybackProgressReport
{
    public string ItemId { get; init; } = string.Empty;
    public string? MediaSourceId { get; init; }
    public string? PlaySessionId { get; init; }
    public long PositionTicks { get; init; }
    public long? RunTimeTicks { get; init; }
    public bool IsPaused { get; init; }
    public bool IsMuted { get; init; }
    public int? VolumeLevel { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
}
```

## 9. Domain Models for UI

UI view models should use app-level models, not raw API DTOs.

### MediaItemSummary

```csharp
public sealed class MediaItemSummary
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int? Year { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public double? PlayedPercentage { get; init; }
    public bool IsPlayed { get; init; }
    public bool IsFavorite { get; init; }
    public long? RunTimeTicks { get; init; }
}
```

### MediaPerson

```csharp
public sealed class MediaPerson
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
}
```

Movie and Series detail preserve server order, keep unknown person types, and limit the mapped people list to 24. Basic artwork combines de-duplicated backdrop tags and `ImageTags.Art`, preserves source order, and is limited to 12 thumbnail URLs.

### PlaybackInfo

```csharp
public sealed class PlaybackInfo
{
    public string ItemId { get; init; } = string.Empty;
    public string? MediaSourceId { get; init; }
    public string? PlaySessionId { get; init; }
    public Uri StreamUri { get; init; } = default!;
    public IReadOnlyList<MediaStreamInfo> AudioStreams { get; init; } = Array.Empty<MediaStreamInfo>();
    public IReadOnlyList<MediaStreamInfo> SubtitleStreams { get; init; } = Array.Empty<MediaStreamInfo>();
    public long? RunTimeTicks { get; init; }
    public long? ResumePositionTicks { get; init; }
    public bool RequiresTranscoding { get; init; }
}
```

## 10. Image URL Rules

Image URLs should be generated by a single service, for example `IEmbyImageUrlBuilder`.

Examples:

```text
/Items/{Id}/Images/Primary
/Items/{Id}/Images/Backdrop/0
/Users/{UserId}/Images/Primary
```

Rules:

- UI never builds raw image URLs by hand.
- Missing image returns placeholder.
- Failed image load should not mark the whole page failed.
- Use reasonable image sizes to avoid loading huge artwork in card grids.
- Similar posters use a card-sized Primary image request and never put the access token in the URL.

`EmbyImageService` stores successful image bytes in memory only. Cache usage is the sum of live cached byte arrays, not decoded WPF surfaces or disk files. Clearing swaps out this cache and notifies image controls; an older in-flight request cannot repopulate the new cache or overwrite a control with stale bytes. Detached controls reload after the cache generation changes.

## 11. Error Mapping

| HTTP / Error | App Error | UI Behavior |
|---|---|---|
| timeout | ServerTimeout | show timeout message and retry |
| DNS/connect fail | ServerUnreachable | show server unavailable |
| 400 | BadRequest | log details, show generic error |
| 401 | Unauthorized | clear token, navigate login |
| 403 | Forbidden | show permission denied |
| 404 | NotFound | show item unavailable |
| 5xx | ServerError | show retry |
| invalid JSON | InvalidResponse | log and show generic error |
| cancellation | Cancelled | no scary error; user cancelled |

## 12. Pagination Rules

- Default page size: 50.
- Never request entire large libraries at once.
- Track `StartIndex`, `Limit`, and `TotalRecordCount` where available.
- UI should request next page near scroll end.
- Avoid duplicate items when pagination responses overlap.

The current library feature service, `ILibraryService` / `EmbyLibraryService`, accepts an immutable `LibraryQuery` with name/date-added/year sorting, ascending/descending direction, all/unwatched/watched selection, and favorites-only. Its original overload forwards to default name-ascending/unfiltered options. The library UI retains its existing maximum page size of 100.

`GET /Users/{UserId}/Items` maps those sort fields to `SortName`, `DateCreated,SortName`, and `ProductionYear,SortName` respectively; the secondary name sort gives date/year ties a consistent order. `SortOrder` is `Ascending` or `Descending`. Watched selection sends `IsPlayed=false` or `IsPlayed=true`; all omits that parameter. Favorites-only adds `IsFavorite=true`; all omits it. These filters combine in the same authenticated request and apply before pagination. The query retains the existing parent/type/recursive fields and explicitly enables user data. See the [official item query reference](https://dev.emby.media/reference/RestAPI/ItemsService/getUsersByUseridItems.html).

Every page retains the same query snapshot. Changing query, library, or session resets the offset and cancels the old request; a generation/session check rejects late success, failure, or unauthorized responses. Overlapping item IDs are displayed once, while the next offset follows the server page boundary. A reached total or empty page ends pagination.

## 13. Search Rules

- Debounce input around 300 ms.
- Cancel previous search when a newer query starts.
- Ignore stale responses from older searches.
- Search uses current user context.
- Do not search empty strings except for explicit reset behavior.

Local search-index maintenance uses the existing paginated item read path (400 source rows per page), never a server-library scan. Disk files remain scoped by server/user hashes. Usage reads, disk hydration, deletion, and final file replacement share an in-process gate; clearing invalidates all in-memory scopes and cancels older builds before deleting only recognized index/temp files in the configured application cache directory. Do not traverse redirected directories or delete credentials/preferences. Explicit rebuild clears local indexes and rebuilds the current account with cancellation and recoverable storage/network/invalid-response failures; a current 401 expires that session. A canceled rebuild must not later commit or prevent a subsequent on-demand search from rebuilding.

## 14. Progress Ticks

Emby playback positions use ticks.

Use helper methods:

```csharp
public static long SecondsToTicks(double seconds) => (long)(seconds * 10_000_000);
public static double TicksToSeconds(long ticks) => ticks / 10_000_000.0;
```

Do not scatter tick conversion code across the app.

## 15. Progress Reporting Rules

- Report start after playback actually begins.
- Report progress every 5 to 10 seconds while playing.
- Report immediately when pause/resume changes.
- Report stopped on player exit.
- Do not block playback if progress report fails.
- Use cancellation token on app/player shutdown.
- A playback-report `401` expires the current session; `403` is a permission failure and must not clear the session or stop local playback.
- Keep caller cancellation distinct from timeout, and keep timeout, server-unreachable, and `5xx` failures recoverable so local playback continues.

## 16. Mock Server Requirements for Tests

Tests should not require a real Emby Server.

Create fake HTTP responses for:

- public server info success
- authentication success
- authentication failure
- token expired
- empty library
- movie list
- series/season/episode list
- playback info with one audio and one subtitle
- playback info with multiple audio/subtitle streams
- progress report success
- progress report failure
- similar items success, empty result, route-prefix fallback, cancellation, and each mapped error category

## 17. API Do Not Rules

Do not:

- call HTTP endpoints from UI code-behind
- duplicate URL building in multiple classes
- store password
- store plain token
- log token
- request whole library without pagination
- treat missing image as page failure
- crash on invalid server response
- silently ignore 401
- mix remote control Sessions API with local playback progress unless explicitly required

## 18. Reference Notes

Timeline previews use authenticated `GET /Videos/{Id}/index.bif?Width=320`, documented by [Emby BifService](https://dev.emby.media/reference/RestAPI/BifService/getVideosByIdIndexBif.html). The new preview transport goes through `IEmbyApiClient` / `EmbyApiClient`; caching remains in `EmbyPlaybackPreviewService`. A 404 may retry the existing `/emby` base convention once, while 401/403 remain distinct errors. Requests are cancellable, time out after 10 seconds, and cap archive downloads at 32 MiB. Parse the [Roku BIF v0 index](https://developer.roku.com/dev/docs/bif-file-creation) with bounded offsets/timestamps and a 2 MiB encoded-frame limit. A valid empty archive is unavailable, not a playback failure. No preview generation or server scan is triggered.

Official Emby documentation states that user authentication uses `/Users/AuthenticateByName`, successful authentication returns an `AccessToken`, and subsequent requests should include `X-Emby-Token`.

Official Emby REST reference documents `/Items`, `/Items/{Id}/PlaybackInfo`, and `/Sessions/Playing/Progress` for item queries, playback info, and playback progress reporting.
