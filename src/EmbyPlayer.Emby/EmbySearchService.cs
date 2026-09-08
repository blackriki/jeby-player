using System.Net;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Search;

namespace EmbyPlayer.Emby;

public sealed class EmbySearchService : ISearchService
{
    private const int SearchLimit = 50;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;
    private readonly ILocalMediaSearchIndex localMediaSearchIndex;

    public EmbySearchService(
        HttpClient httpClient,
        IDeviceIdService deviceIdService,
        ILocalMediaSearchIndex localMediaSearchIndex)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
        this.localMediaSearchIndex = localMediaSearchIndex
            ?? throw new ArgumentNullException(nameof(localMediaSearchIndex));
    }

    public async Task<SearchLoadResult> SearchAsync(
        AuthSession session,
        string keyword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return SearchLoadResult.Success(Array.Empty<SearchResultItem>());
        }

        var serverSearchTask = GetWithFallbackAsync(
            session,
            BuildSearchPath(session.UserId, keyword),
            cancellationToken);
        var localSearchTask = SearchLocalIndexAsync(session, keyword, cancellationToken);

        try
        {
            await Task.WhenAll(serverSearchTask, localSearchTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SearchLoadResult.Failure(SearchLoadError.Cancelled);
        }

        var contentResult = await serverSearchTask.ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return SearchLoadResult.Failure(contentResult.Error);
        }

        var items = ParseItems(contentResult.Content!, contentResult.ApiBase!);
        if (items is null)
        {
            return SearchLoadResult.Failure(SearchLoadError.InvalidResponse);
        }

        var localResult = await localSearchTask.ConfigureAwait(false);
        var mergedItems = MergeSearchResults(keyword, items, localResult);
        return SearchLoadResult.Success(
            mergedItems,
            new SearchExecutionDiagnostics(
                items.Count,
                localResult.Matches.Count,
                mergedItems.Count,
                localResult.TotalIndexedItems,
                localResult.LastBuildDuration,
                localResult.LastLoadDuration));
    }

    public async Task<SearchLoadResult> LoadFavoritesAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var contentResult = await GetWithFallbackAsync(
                session,
                BuildFavoritesPath(session.UserId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return SearchLoadResult.Failure(contentResult.Error);
        }

        var items = ParseItems(contentResult.Content!, contentResult.ApiBase!);
        if (items is null)
        {
            return SearchLoadResult.Failure(SearchLoadError.InvalidResponse);
        }

        return SearchLoadResult.Success(items);
    }

    private async Task<LocalMediaSearchQueryResult> SearchLocalIndexAsync(
        AuthSession session,
        string keyword,
        CancellationToken cancellationToken)
    {
        try
        {
            return await localMediaSearchIndex
                .SearchAsync(session, keyword, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Local media search index query failed: {exception.Message}");
            return LocalMediaSearchQueryResult.Empty;
        }
    }

    private static IReadOnlyList<SearchResultItem> MergeSearchResults(
        string keyword,
        IReadOnlyList<SearchResultItem> serverItems,
        LocalMediaSearchQueryResult localResult)
    {
        var normalizedKeyword = Normalize(keyword);
        var candidates = new Dictionary<string, RankedSearchItem>(StringComparer.Ordinal);

        foreach (var item in serverItems)
        {
            candidates[item.Id] = new RankedSearchItem(item, GetTitleRank(item.Title, normalizedKeyword));
        }

        foreach (var match in localResult.Matches)
        {
            var matchInfo = CreateMatchInfo(match);
            if (candidates.TryGetValue(match.Item.Id, out var existing))
            {
                candidates[match.Item.Id] = existing with
                {
                    Item = matchInfo is null
                        ? existing.Item
                        : existing.Item with { MatchInfo = matchInfo },
                    Rank = Math.Min(existing.Rank, match.Rank)
                };
                continue;
            }

            candidates[match.Item.Id] = new RankedSearchItem(
                MapIndexedItem(match.Item, localResult.ApiBase, matchInfo),
                match.Rank);
        }

        return candidates.Values
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => GetMediaTypeRank(candidate.Item.Type))
            .ThenBy(candidate => candidate.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(candidate => candidate.Item.Year)
            .ThenBy(candidate => candidate.Item.Id, StringComparer.Ordinal)
            .Select(candidate => candidate.Item)
            .ToArray();
    }

    private static SearchResultItem MapIndexedItem(
        LocalMediaSearchItem item,
        string? apiBase,
        SearchMatchInfo? matchInfo)
    {
        return new SearchResultItem(
            item.Id,
            GetDisplayTitle(item),
            item.Type,
            item.ProductionYear,
            BuildPosterUrl(apiBase, item),
            null,
            MatchInfo: matchInfo);
    }

    private static SearchMatchInfo? CreateMatchInfo(LocalMediaSearchMatch match)
    {
        return match.MatchSource == SearchMatchSource.Unknown
            ? null
            : new SearchMatchInfo(match.MatchSource, match.Item.SeriesName);
    }

    private static int GetMediaTypeRank(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "movie" or "series" or "boxset" or "playlist" or "playlists" or "video" => 0,
            "episode" => 1,
            _ => 2
        };
    }

    private static int GetTitleRank(string title, string normalizedKeyword)
    {
        var normalizedTitle = Normalize(title);
        if (string.Equals(normalizedTitle, normalizedKeyword, StringComparison.Ordinal))
        {
            return 0;
        }

        if (normalizedTitle.StartsWith(normalizedKeyword, StringComparison.Ordinal))
        {
            return 1;
        }

        return normalizedTitle.Contains(normalizedKeyword, StringComparison.Ordinal) ? 2 : 5;
    }

    private static string Normalize(string value)
    {
        return value.Trim().Normalize(NormalizationForm.FormKC).ToUpper(CultureInfo.InvariantCulture);
    }

    private async Task<SearchEndpointResult> GetWithFallbackAsync(
        AuthSession session,
        string endpointPath,
        CancellationToken cancellationToken)
    {
        var serverBase = session.ServerBase.TrimEnd('/');
        var primaryResult = await GetStringAsync(
                BuildEndpointUri(serverBase, endpointPath),
                session.AccessToken,
                cancellationToken)
            .ConfigureAwait(false);

        if (!primaryResult.CanFallback)
        {
            return primaryResult.WithApiBase(serverBase);
        }

        var embyApiBase = $"{serverBase}/emby";
        var fallbackResult = await GetStringAsync(
                BuildEndpointUri(embyApiBase, endpointPath),
                session.AccessToken,
                cancellationToken)
            .ConfigureAwait(false);

        return fallbackResult.WithApiBase(embyApiBase);
    }

    private async Task<SearchEndpointResult> GetStringAsync(
        Uri endpointUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);
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
                return SearchEndpointResult.NoFallback(SearchLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return SearchEndpointResult.NoFallback(SearchLoadError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return SearchEndpointResult.FallbackAllowed(SearchLoadError.ServerError);
            }

            if (!response.IsSuccessStatusCode)
            {
                return SearchEndpointResult.NoFallback(SearchLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(content)
                ? SearchEndpointResult.NoFallback(SearchLoadError.InvalidResponse)
                : SearchEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SearchEndpointResult.NoFallback(SearchLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return SearchEndpointResult.NoFallback(SearchLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return SearchEndpointResult.NoFallback(SearchLoadError.ServerUnreachable);
        }
    }

    private static IReadOnlyList<SearchResultItem>? ParseItems(string content, string apiBase)
    {
        try
        {
            var items = JsonSerializer.Deserialize<ItemListResponse>(content, JsonOptions)?.Items;
            if (items is null)
            {
                return Array.Empty<SearchResultItem>();
            }

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new SearchResultItem(
                    item.Id!,
                    GetDisplayTitle(item),
                    item.Type ?? string.Empty,
                    item.ProductionYear,
                    BuildPosterUrl(apiBase, item),
                    GetPlayedPercentage(item),
                    item.UserData?.IsFavorite ?? false,
                    item.UserData?.Played ?? false))
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildSearchPath(string userId, string keyword)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items"
            + $"?SearchTerm={Uri.EscapeDataString(keyword.Trim())}"
            + "&Recursive=true"
            + "&IncludeItemTypes=Movie,Series,Episode,BoxSet,Video"
            + $"&Limit={SearchLimit}"
            + "&Fields=UserData,PrimaryImageAspectRatio,ProductionYear,SeriesName,IndexNumber,ParentIndexNumber,RunTimeTicks"
            + "&SortBy=SortName&SortOrder=Ascending";
    }

    private static string BuildFavoritesPath(string userId)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items"
            + "?Recursive=true"
            + "&Filters=IsFavorite"
            + "&IncludeItemTypes=Movie,Series,Episode,BoxSet,Video"
            + $"&Limit={SearchLimit}"
            + "&Fields=UserData,PrimaryImageAspectRatio,ProductionYear,SeriesName,IndexNumber,ParentIndexNumber,RunTimeTicks"
            + "&SortBy=SortName&SortOrder=Ascending";
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
            var episodeNumber = GetEpisodeNumberText(item);
            return string.IsNullOrWhiteSpace(episodeNumber)
                ? item.SeriesName!
                : $"{item.SeriesName} {episodeNumber}";
        }

        return item.Name ?? string.Empty;
    }

    private static string GetDisplayTitle(LocalMediaSearchItem item)
    {
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.SeriesName))
        {
            var episodeNumber = GetEpisodeNumberText(item.ParentIndexNumber, item.IndexNumber);
            return string.IsNullOrWhiteSpace(episodeNumber)
                ? item.SeriesName
                : $"{item.SeriesName} {episodeNumber}";
        }

        return item.Name;
    }

    private static string GetEpisodeNumberText(ItemResponse item)
    {
        return GetEpisodeNumberText(item.ParentIndexNumber, item.IndexNumber);
    }

    private static string GetEpisodeNumberText(int? parentIndexNumber, int? indexNumber)
    {
        if (parentIndexNumber is not null && indexNumber is not null)
        {
            return $"S{parentIndexNumber}:E{indexNumber}";
        }

        if (indexNumber is not null)
        {
            return $"E{indexNumber}";
        }

        return string.Empty;
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

    private static string? BuildPosterUrl(string? apiBase, LocalMediaSearchItem item)
    {
        if (string.IsNullOrWhiteSpace(apiBase)
            || string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.PrimaryImageTag))
        {
            return null;
        }

        return BuildEndpointUri(
                apiBase,
                $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Primary?maxHeight=450&quality=90")
            .ToString();
    }

    private static Uri BuildEndpointUri(string apiBase, string endpointPath)
    {
        return new Uri($"{apiBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private sealed class ItemListResponse
    {
        public List<ItemResponse>? Items { get; set; }
    }

    private sealed class ItemResponse
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public int? ProductionYear { get; set; }

        public long? RunTimeTicks { get; set; }

        public string? SeriesName { get; set; }

        public int? IndexNumber { get; set; }

        public int? ParentIndexNumber { get; set; }

        public ItemImageTags? ImageTags { get; set; }

        public ItemUserData? UserData { get; set; }
    }

    private sealed class ItemImageTags
    {
        public string? Primary { get; set; }
    }

    private sealed class ItemUserData
    {
        public double? PlayedPercentage { get; set; }

        public long? PlaybackPositionTicks { get; set; }

        public bool? IsFavorite { get; set; }

        public bool? Played { get; set; }
    }

    private sealed record RankedSearchItem(SearchResultItem Item, int Rank);

    private sealed class SearchEndpointResult
    {
        private SearchEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            SearchLoadError error,
            string? apiBase)
        {
            IsSuccess = isSuccess;
            CanFallback = canFallback;
            Content = content;
            Error = error;
            ApiBase = apiBase;
        }

        public bool IsSuccess { get; }

        public bool CanFallback { get; }

        public string? Content { get; }

        public SearchLoadError Error { get; }

        public string? ApiBase { get; }

        public SearchEndpointResult WithApiBase(string apiBase)
        {
            return new SearchEndpointResult(IsSuccess, CanFallback, Content, Error, apiBase);
        }

        public static SearchEndpointResult Success(string content)
        {
            return new SearchEndpointResult(true, false, content, SearchLoadError.None, null);
        }

        public static SearchEndpointResult FallbackAllowed(SearchLoadError error)
        {
            return new SearchEndpointResult(false, true, null, error, null);
        }

        public static SearchEndpointResult NoFallback(SearchLoadError error)
        {
            return new SearchEndpointResult(false, false, null, error, null);
        }
    }
}
