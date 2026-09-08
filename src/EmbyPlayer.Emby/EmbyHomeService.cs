using System.Net;
using System.Globalization;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Home;

namespace EmbyPlayer.Emby;

public sealed class EmbyHomeService : IHomeService
{
    private const int ContinueWatchingLimit = 24;
    private const int RecentlyAddedLimit = 24;
    private const int HomeSectionLimit = RecentlyAddedLimit;
    private const int SectionPageLimit = 100;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyHomeService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<HomeLoadResult> LoadHomeAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var serverBase = session.ServerBase.TrimEnd('/');
        var apiBase = serverBase;
        var librariesResult = await GetStringAsync(
                BuildEndpointUri(apiBase, BuildLibrariesPath(session.UserId)),
                session.AccessToken,
                deviceId,
                cancellationToken)
            .ConfigureAwait(false);

        if (librariesResult.CanFallback)
        {
            apiBase = $"{serverBase}/emby";
            librariesResult = await GetStringAsync(
                    BuildEndpointUri(apiBase, BuildLibrariesPath(session.UserId)),
                    session.AccessToken,
                    deviceId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!librariesResult.IsSuccess)
        {
            return HomeLoadResult.Failure(librariesResult.Error);
        }

        var libraries = ParseLibraries(librariesResult.Content!);
        if (libraries is null)
        {
            return HomeLoadResult.Failure(HomeLoadError.InvalidResponse);
        }

        // Once Views confirms the route and libraries, these rows have no data dependencies.
        var continueWatchingTask = GetStringAsync(
                BuildEndpointUri(apiBase, BuildContinueWatchingPath(session.UserId)),
                session.AccessToken,
                deviceId,
                cancellationToken);
        var recentlyAddedTask = GetStringAsync(
                BuildEndpointUri(apiBase, BuildRecentlyAddedPath(session.UserId)),
                session.AccessToken,
                deviceId,
                cancellationToken);

        var animationLibraryIds = libraries
            .Where(MediaLibraryClassifier.IsAnimationLibrary)
            .Select(library => library.Id)
            .ToArray();
        var sectionRequests = new[]
        {
            new HomeSectionRequest("movies", "电影", "Movie", Array.Empty<string>()),
            new HomeSectionRequest("series", "电视节目", "Series", Array.Empty<string>()),
            new HomeSectionRequest("boxsets", "合集", "BoxSet", Array.Empty<string>())
        }.ToList();
        if (animationLibraryIds.Length > 0)
        {
            sectionRequests.Insert(2, new HomeSectionRequest(
                "animation",
                "动画",
                "Movie,Series",
                animationLibraryIds));
        }

        var sectionTasks = sectionRequests
            .Select(request => LoadMediaSectionAsync(apiBase, session, deviceId, request, cancellationToken))
            .ToArray();
        await Task.WhenAll(new Task[] { continueWatchingTask, recentlyAddedTask }.Concat(sectionTasks))
            .ConfigureAwait(false);
        var continueWatchingResult = await continueWatchingTask.ConfigureAwait(false);
        var recentlyAddedResult = await recentlyAddedTask.ConfigureAwait(false);
        var sectionResults = await Task.WhenAll(sectionTasks).ConfigureAwait(false);
        var errors = new[] { continueWatchingResult.Error, recentlyAddedResult.Error }
            .Concat(sectionResults.Select(result => result.Error)).ToArray();
        if (errors.Contains(HomeLoadError.Unauthorized))
        {
            return HomeLoadResult.Failure(HomeLoadError.Unauthorized);
        }
        if (errors.Contains(HomeLoadError.Cancelled))
        {
            return HomeLoadResult.Failure(HomeLoadError.Cancelled);
        }
        if (!continueWatchingResult.IsSuccess)
        {
            return HomeLoadResult.Failure(continueWatchingResult.Error);
        }
        var continueWatching = ParseItems(continueWatchingResult.Content!, apiBase, expectsItemsWrapper: true);
        if (continueWatching is null)
        {
            return HomeLoadResult.Failure(HomeLoadError.InvalidResponse);
        }
        if (!recentlyAddedResult.IsSuccess)
        {
            return HomeLoadResult.Failure(recentlyAddedResult.Error);
        }

        var recentlyAdded = ParseItems(recentlyAddedResult.Content!, apiBase, expectsItemsWrapper: false);
        if (recentlyAdded is null)
        {
            return HomeLoadResult.Failure(HomeLoadError.InvalidResponse);
        }

        return HomeLoadResult.Success(new HomeData(
            session.UserName,
            continueWatching,
            recentlyAdded,
            libraries,
            sectionResults.Select(result => result.Section).ToArray()));
    }

    public async Task<HomeSectionItemsLoadResult> LoadSectionAsync(
        AuthSession session,
        HomeSectionKind section,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var safeStartIndex = Math.Max(0, startIndex);
        var safeLimit = Math.Clamp(limit, 1, SectionPageLimit);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deviceId = await deviceIdService
                .GetOrCreateDeviceIdAsync(cancellationToken)
                .ConfigureAwait(false);
            if (section == HomeSectionKind.Animation)
            {
                return await LoadAnimationPageAsync(session, deviceId, safeStartIndex, safeLimit, cancellationToken)
                    .ConfigureAwait(false);
            }

            var endpointPath = section switch
            {
                HomeSectionKind.ContinueWatching => BuildContinueWatchingPath(session.UserId, safeStartIndex, safeLimit),
                HomeSectionKind.RecentlyAdded => BuildRecentlyAddedPath(session.UserId, safeStartIndex, safeLimit),
                HomeSectionKind.Movies => BuildMediaSectionPath(session.UserId, "Movie", null, safeStartIndex, safeLimit),
                HomeSectionKind.Series => BuildMediaSectionPath(session.UserId, "Series", null, safeStartIndex, safeLimit),
                HomeSectionKind.BoxSets => BuildMediaSectionPath(session.UserId, "BoxSet", null, safeStartIndex, safeLimit),
                _ => null
            };
            if (endpointPath is null)
            {
                return HomeSectionItemsLoadResult.Failure(HomeLoadError.InvalidResponse);
            }

            var (result, apiBase) = await GetWithFallbackAsync(session, deviceId, endpointPath, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return HomeSectionItemsLoadResult.Failure(result.Error);
            }

            var page = ParseItemPage(result.Content!, apiBase, section != HomeSectionKind.RecentlyAdded);
            if (page is null)
            {
                return HomeSectionItemsLoadResult.Failure(HomeLoadError.InvalidResponse);
            }

            var nextStartIndex = safeStartIndex + page.RawCount;
            var hasMore = page.RawCount > 0 && (page.TotalRecordCount is int total
                ? nextStartIndex < total
                : page.RawCount >= safeLimit);
            return HomeSectionItemsLoadResult.Success(page.Items, nextStartIndex, hasMore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HomeSectionItemsLoadResult.Failure(HomeLoadError.Cancelled);
        }
    }

    private async Task<HomeSectionItemsLoadResult> LoadAnimationPageAsync(
        AuthSession session,
        string deviceId,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        var (librariesResult, apiBase) = await GetWithFallbackAsync(
                session, deviceId, BuildLibrariesPath(session.UserId), cancellationToken)
            .ConfigureAwait(false);
        if (!librariesResult.IsSuccess)
        {
            return HomeSectionItemsLoadResult.Failure(librariesResult.Error);
        }

        var libraries = ParseLibraries(librariesResult.Content!);
        if (libraries is null)
        {
            return HomeSectionItemsLoadResult.Failure(HomeLoadError.InvalidResponse);
        }

        var prefixCount = startIndex + limit + 1;
        var libraryTasks = libraries
            .Where(MediaLibraryClassifier.IsAnimationLibrary)
            .DistinctBy(library => library.Id, StringComparer.OrdinalIgnoreCase)
            .Select(library => LoadAnimationPrefixAsync(
                apiBase, session, deviceId, library.Id, prefixCount, cancellationToken));
        var results = await Task.WhenAll(libraryTasks).ConfigureAwait(false);
        var failure = results.FirstOrDefault(result => result.Error is HomeLoadError.Unauthorized or HomeLoadError.Cancelled)
            ?? results.FirstOrDefault(result => result.Error != HomeLoadError.None);
        if (failure is not null)
        {
            return HomeSectionItemsLoadResult.Failure(failure.Error);
        }

        var mergedItems = results
            .SelectMany(result => result.Items)
            .OrderByDescending(item => item.DateCreated)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var items = mergedItems.Skip(startIndex).Take(limit).ToArray();
        var nextStartIndex = startIndex + items.Length;
        return HomeSectionItemsLoadResult.Success(items, nextStartIndex, mergedItems.Length > nextStartIndex);
    }

    private async Task<HomeSectionItemResult> LoadAnimationPrefixAsync(
        string apiBase,
        AuthSession session,
        string deviceId,
        string libraryId,
        int prefixCount,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaCard>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        var startIndex = 0;
        while (items.Count < prefixCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Min(SectionPageLimit, prefixCount - items.Count);
            var result = await GetStringAsync(
                    BuildEndpointUri(apiBase, BuildMediaSectionPath(session.UserId, "Movie,Series", libraryId, startIndex, limit)),
                    session.AccessToken,
                    deviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return HomeSectionItemResult.Failure(result.Error);
            }

            var page = ParseItemPage(result.Content!, apiBase, expectsItemsWrapper: true);
            if (page is null)
            {
                return HomeSectionItemResult.Failure(HomeLoadError.InvalidResponse);
            }

            if (page.RawCount == 0 || !seenPages.Add(result.Content!))
            {
                break;
            }

            items.AddRange(page.Items.Where(item => seenIds.Add(item.Id)));
            startIndex += page.RawCount;
            if (page.TotalRecordCount is int total ? startIndex >= total : page.RawCount < limit)
            {
                break;
            }
        }

        return HomeSectionItemResult.Success(items);
    }

    private async Task<(HomeEndpointResult Result, string ApiBase)> GetWithFallbackAsync(
        AuthSession session,
        string deviceId,
        string endpointPath,
        CancellationToken cancellationToken)
    {
        var apiBase = session.ServerBase.TrimEnd('/');
        var result = await GetStringAsync(
                BuildEndpointUri(apiBase, endpointPath), session.AccessToken, deviceId, cancellationToken)
            .ConfigureAwait(false);
        if (result.CanFallback)
        {
            apiBase += "/emby";
            result = await GetStringAsync(
                    BuildEndpointUri(apiBase, endpointPath), session.AccessToken, deviceId, cancellationToken)
                .ConfigureAwait(false);
        }

        return (result, apiBase);
    }

    private async Task<HomeSectionLoadResult> LoadMediaSectionAsync(
        string apiBase,
        AuthSession session,
        string deviceId,
        HomeSectionRequest request,
        CancellationToken cancellationToken)
    {
        var parentIds = request.ParentIds.Count == 0
            ? new string?[] { null }
            : request.ParentIds.Cast<string?>().ToArray();
        var itemResults = await Task.WhenAll(parentIds.Select(parentId =>
                LoadSectionItemsAsync(apiBase, session, deviceId, request.IncludeItemTypes, parentId, cancellationToken)))
            .ConfigureAwait(false);
        var criticalFailure = itemResults.FirstOrDefault(result =>
            result.Error is HomeLoadError.Unauthorized or HomeLoadError.Cancelled);
        if (criticalFailure is not null)
        {
            return HomeSectionLoadResult.Failure(request.Id, request.Title, criticalFailure.Error);
        }

        if (itemResults.Any(result => result.Error != HomeLoadError.None))
        {
            return HomeSectionLoadResult.Failure(request.Id, request.Title, HomeLoadError.None);
        }

        var items = itemResults
            .SelectMany(result => result.Items)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.DateCreated)
            .Take(HomeSectionLimit)
            .ToArray();
        return HomeSectionLoadResult.Success(request.Id, request.Title, items);
    }

    private async Task<HomeSectionItemResult> LoadSectionItemsAsync(
        string apiBase,
        AuthSession session,
        string deviceId,
        string includeItemTypes,
        string? parentId,
        CancellationToken cancellationToken)
    {
        var result = await GetStringAsync(
                BuildEndpointUri(apiBase, BuildMediaSectionPath(session.UserId, includeItemTypes, parentId)),
                session.AccessToken,
                deviceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return HomeSectionItemResult.Failure(result.Error);
        }

        var items = ParseItems(result.Content!, apiBase, expectsItemsWrapper: true);
        return items is null
            ? HomeSectionItemResult.Failure(HomeLoadError.InvalidResponse)
            : HomeSectionItemResult.Success(items);
    }

    private async Task<HomeEndpointResult> GetStringAsync(
        Uri endpointUri,
        string accessToken,
        string deviceId,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUri);
            request.Headers.TryAddWithoutValidation("X-Emby-Token", accessToken);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return HomeEndpointResult.NoFallback(HomeLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return HomeEndpointResult.NoFallback(HomeLoadError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return HomeEndpointResult.FallbackAllowed(HomeLoadError.ServerError);
            }

            if (!response.IsSuccessStatusCode)
            {
                return HomeEndpointResult.NoFallback(HomeLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(content)
                ? HomeEndpointResult.NoFallback(HomeLoadError.InvalidResponse)
                : HomeEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HomeEndpointResult.NoFallback(HomeLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return HomeEndpointResult.NoFallback(HomeLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return HomeEndpointResult.NoFallback(HomeLoadError.ServerUnreachable);
        }
    }

    private static IReadOnlyList<MediaLibrary>? ParseLibraries(string content)
    {
        LibraryResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<LibraryResponse>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (response?.Items is null)
        {
            return Array.Empty<MediaLibrary>();
        }

        return response.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new MediaLibrary(
                item.Id!,
                item.Name!,
                item.CollectionType ?? item.Type ?? string.Empty))
            .ToArray();
    }

    private static IReadOnlyList<MediaCard>? ParseItems(
        string content,
        string apiBase,
        bool expectsItemsWrapper)
    {
        return ParseItemPage(content, apiBase, expectsItemsWrapper)?.Items;
    }

    private static ParsedHomeItems? ParseItemPage(
        string content,
        string apiBase,
        bool expectsItemsWrapper)
    {
        try
        {
            var response = expectsItemsWrapper
                ? JsonSerializer.Deserialize<ItemListResponse>(content, JsonOptions)
                : null;
            var items = expectsItemsWrapper
                ? response?.Items
                : JsonSerializer.Deserialize<List<ItemResponse>>(content, JsonOptions);

            if (items is null)
            {
                return new ParsedHomeItems(Array.Empty<MediaCard>(), 0, response?.TotalRecordCount);
            }

            var cards = items
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new MediaCard(
                    item.Id!,
                    GetDisplayTitle(item),
                    item.Type ?? string.Empty,
                    item.ProductionYear,
                    BuildPosterUrl(apiBase, item),
                    GetPlayedPercentage(item),
                    GetDisplaySubtitle(item),
                    item.UserData?.IsFavorite ?? false,
                    item.UserData?.Played ?? false,
                    BuildHeroImageUrl(apiBase, item),
                    item.UserData?.PlaybackPositionTicks,
                    item.DateCreated,
                    item.SeriesId,
                    item.SeasonId ?? item.ParentId,
                    BuildLogoUrl(apiBase, item)))
                .ToArray();
            return new ParsedHomeItems(cards, items.Count, response?.TotalRecordCount);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? GetPlayedPercentage(ItemResponse item)
    {
        if (item.UserData?.PlayedPercentage is > 0 and < 100)
        {
            return item.UserData.PlayedPercentage;
        }

        if (item.UserData?.PlaybackPositionTicks is > 0
            && item.RunTimeTicks is > 0)
        {
            var percentage = item.UserData.PlaybackPositionTicks.Value * 100d / item.RunTimeTicks.Value;
            return Math.Clamp(percentage, 0d, 100d);
        }

        return null;
    }

    private static string GetDisplayTitle(ItemResponse item)
    {
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.SeriesName))
        {
            return item.SeriesName!;
        }

        return item.Name ?? string.Empty;
    }

    private static string GetDisplaySubtitle(ItemResponse item)
    {
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>();
            var episodeNumber = GetEpisodeNumberText(item);
            if (!string.IsNullOrWhiteSpace(episodeNumber))
            {
                parts.Add(episodeNumber);
            }

            if (!string.IsNullOrWhiteSpace(item.Name)
                && !ContainsSeriesName(item.Name, item.SeriesName)
                && !ContainsEpisodeNumber(item.Name, item))
            {
                parts.Add(item.Name!);
            }

            return string.Join(" · ", parts);
        }

        if (item.ProductionYear is not null)
        {
            return string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase)
                ? $"{item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture)} · 电影"
                : item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture);
        }

        return string.Empty;
    }

    private static string GetEpisodeNumberText(ItemResponse item)
    {
        if (item.ParentIndexNumber is not null && item.IndexNumber is not null)
        {
            return $"S{item.ParentIndexNumber}:E{item.IndexNumber}";
        }

        if (item.IndexNumber is not null)
        {
            return $"E{item.IndexNumber}";
        }

        return string.Empty;
    }

    private static bool ContainsEpisodeNumber(string value, ItemResponse item)
    {
        if (item.IndexNumber is not null
            && value.Contains($"E{item.IndexNumber.Value}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return item.ParentIndexNumber is not null
            && item.IndexNumber is not null
            && (value.Contains($"S{item.ParentIndexNumber.Value}:E{item.IndexNumber.Value}", StringComparison.OrdinalIgnoreCase)
                || value.Contains($"S{item.ParentIndexNumber.Value:00}E{item.IndexNumber.Value:00}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSeriesName(string value, string? seriesName)
    {
        return !string.IsNullOrWhiteSpace(seriesName)
            && value.Contains(seriesName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildPosterUrl(string apiBase, ItemResponse item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.ImageTags?.Primary))
        {
            return null;
        }

        return BuildEndpointUri(
                apiBase,
                $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Primary?maxHeight=450&quality=90")
            .ToString();
    }

    private static string? BuildHeroImageUrl(string apiBase, ItemResponse item)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            return null;
        }

        var itemId = Uri.EscapeDataString(item.Id);
        if (item.BackdropImageTags is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(item.BackdropImageTags[0]))
        {
            return BuildEndpointUri(
                    apiBase,
                    $"/Items/{itemId}/Images/Backdrop/0?maxWidth=1600&quality=90")
                .ToString();
        }

        if (!string.IsNullOrWhiteSpace(item.ImageTags?.Thumb))
        {
            return BuildEndpointUri(
                    apiBase,
                    $"/Items/{itemId}/Images/Thumb?maxWidth=1600&quality=90")
                .ToString();
        }

        if (!string.IsNullOrWhiteSpace(item.ImageTags?.Art))
        {
            return BuildEndpointUri(
                    apiBase,
                    $"/Items/{itemId}/Images/Art?maxWidth=1600&quality=90")
                .ToString();
        }

        return BuildPosterUrl(apiBase, item);
    }

    private static string? BuildLogoUrl(string apiBase, ItemResponse item)
    {
        var (logoItemId, logoTag) = !string.IsNullOrWhiteSpace(item.ImageTags?.Logo)
            ? (item.Id, item.ImageTags.Logo)
            : (item.ParentLogoItemId, item.ParentLogoImageTag);
        if (string.IsNullOrWhiteSpace(logoItemId) || string.IsNullOrWhiteSpace(logoTag))
        {
            return null;
        }

        return BuildEndpointUri(
                apiBase,
                $"/Items/{Uri.EscapeDataString(logoItemId)}/Images/Logo?tag={Uri.EscapeDataString(logoTag)}")
            .AbsoluteUri;
    }

    private static string BuildLibrariesPath(string userId)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Views";
    }

    private static string BuildContinueWatchingPath(
        string userId,
        int startIndex = 0,
        int limit = ContinueWatchingLimit)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items"
            + $"?Recursive=true&Filters=IsResumable&StartIndex={startIndex}&Limit={limit}"
            + "&IncludeItemTypes=Movie,Episode,Video"
            + "&Fields=UserData,PrimaryImageAspectRatio,ProductionYear,SeriesName,SeriesId,SeasonId,ParentId,IndexNumber,ParentIndexNumber,RunTimeTicks,ImageTags,BackdropImageTags,ParentLogoItemId,ParentLogoImageTag"
            + "&SortBy=DatePlayed&SortOrder=Descending&EnableUserData=true";
    }

    private static string BuildRecentlyAddedPath(
        string userId,
        int startIndex = 0,
        int limit = RecentlyAddedLimit)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items/Latest"
            + $"?StartIndex={startIndex}&Limit={limit}&GroupItems=true&EnableUserData=true"
            + "&Fields=PrimaryImageAspectRatio,ProductionYear,UserData,RunTimeTicks,ImageTags,BackdropImageTags,ParentLogoItemId,ParentLogoImageTag,SeriesName,SeriesId,SeasonId,ParentId,IndexNumber,ParentIndexNumber,DateCreated";
    }

    private static string BuildMediaSectionPath(
        string userId,
        string includeItemTypes,
        string? parentId,
        int startIndex = 0,
        int limit = HomeSectionLimit)
    {
        var queryParts = new List<string>
        {
            "Recursive=true",
            $"StartIndex={startIndex}",
            $"Limit={limit}",
            $"IncludeItemTypes={includeItemTypes}",
            "Fields=PrimaryImageAspectRatio,ProductionYear,UserData,RunTimeTicks,ImageTags,BackdropImageTags,ParentLogoItemId,ParentLogoImageTag,DateCreated",
            "SortBy=DateCreated",
            "SortOrder=Descending",
            "EnableUserData=true"
        };
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            queryParts.Add($"ParentId={Uri.EscapeDataString(parentId)}");
        }

        return $"/Users/{Uri.EscapeDataString(userId)}/Items?{string.Join("&", queryParts)}";
    }

    private static Uri BuildEndpointUri(string apiBase, string endpointPath)
    {
        return new Uri($"{apiBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private sealed class LibraryResponse
    {
        public List<LibraryItemResponse>? Items { get; set; }
    }

    private sealed class LibraryItemResponse
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public string? CollectionType { get; set; }
    }

    private sealed class ItemListResponse
    {
        public List<ItemResponse>? Items { get; set; }

        public int? TotalRecordCount { get; set; }
    }

    private sealed record ParsedHomeItems(IReadOnlyList<MediaCard> Items, int RawCount, int? TotalRecordCount);

    private sealed class ItemResponse
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public int? ProductionYear { get; set; }

        public DateTimeOffset? DateCreated { get; set; }

        public long? RunTimeTicks { get; set; }

        public string? SeriesName { get; set; }

        public string? SeriesId { get; set; }

        public string? SeasonId { get; set; }

        public string? ParentId { get; set; }

        public int? IndexNumber { get; set; }

        public int? ParentIndexNumber { get; set; }

        public ItemImageTags? ImageTags { get; set; }

        public string? ParentLogoItemId { get; set; }

        public string? ParentLogoImageTag { get; set; }

        public List<string>? BackdropImageTags { get; set; }

        public ItemUserData? UserData { get; set; }
    }

    private sealed record HomeSectionRequest(
        string Id,
        string Title,
        string IncludeItemTypes,
        IReadOnlyList<string> ParentIds);

    private sealed class HomeSectionLoadResult
    {
        private HomeSectionLoadResult(HomeMediaSection section, HomeLoadError error)
        {
            Section = section;
            Error = error;
        }

        public HomeMediaSection Section { get; }

        public HomeLoadError Error { get; }

        public static HomeSectionLoadResult Success(string id, string title, IReadOnlyList<MediaCard> items)
        {
            return new HomeSectionLoadResult(HomeMediaSection.Success(id, title, items), HomeLoadError.None);
        }

        public static HomeSectionLoadResult Failure(string id, string title, HomeLoadError error)
        {
            return new HomeSectionLoadResult(HomeMediaSection.Failure(id, title), error);
        }
    }

    private sealed class HomeSectionItemResult
    {
        private HomeSectionItemResult(IReadOnlyList<MediaCard> items, HomeLoadError error)
        {
            Items = items;
            Error = error;
        }

        public IReadOnlyList<MediaCard> Items { get; }

        public HomeLoadError Error { get; }

        public static HomeSectionItemResult Success(IReadOnlyList<MediaCard> items)
        {
            return new HomeSectionItemResult(items, HomeLoadError.None);
        }

        public static HomeSectionItemResult Failure(HomeLoadError error)
        {
            return new HomeSectionItemResult(Array.Empty<MediaCard>(), error);
        }
    }

    private sealed class ItemImageTags
    {
        public string? Primary { get; set; }

        public string? Thumb { get; set; }

        public string? Art { get; set; }

        public string? Logo { get; set; }
    }

    private sealed class ItemUserData
    {
        public double? PlayedPercentage { get; set; }

        public long? PlaybackPositionTicks { get; set; }

        public bool? IsFavorite { get; set; }

        public bool? Played { get; set; }
    }

    private sealed class HomeEndpointResult
    {
        private HomeEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            HomeLoadError error)
        {
            IsSuccess = isSuccess;
            CanFallback = canFallback;
            Content = content;
            Error = error;
        }

        public bool IsSuccess { get; }

        public bool CanFallback { get; }

        public string? Content { get; }

        public HomeLoadError Error { get; }

        public static HomeEndpointResult Success(string content)
        {
            return new HomeEndpointResult(true, false, content, HomeLoadError.None);
        }

        public static HomeEndpointResult FallbackAllowed(HomeLoadError error)
        {
            return new HomeEndpointResult(false, true, null, error);
        }

        public static HomeEndpointResult NoFallback(HomeLoadError error)
        {
            return new HomeEndpointResult(false, false, null, error);
        }
    }
}
