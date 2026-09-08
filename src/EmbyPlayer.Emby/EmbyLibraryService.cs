using System.Net;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Library;

namespace EmbyPlayer.Emby;

public sealed class EmbyLibraryService : ILibraryService
{
    private const int LibraryItemLimit = 100;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyLibraryService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<LibraryLoadResult> LoadLibrariesAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var contentResult = await GetWithFallbackAsync(
                session,
                BuildLibrariesPath(session.UserId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return LibraryLoadResult.Failure(contentResult.Error);
        }

        var libraries = ParseLibraries(contentResult.Content!);
        return libraries is null
            ? LibraryLoadResult.Failure(LibraryLoadError.InvalidResponse)
            : LibraryLoadResult.Success(libraries);
    }

    public Task<LibraryItemsLoadResult> LoadLibraryItemsAsync(
        AuthSession session,
        LibraryItem library,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        return LoadLibraryItemsAsync(session, library, startIndex, limit, new LibraryQuery(), cancellationToken);
    }

    public async Task<LibraryItemsLoadResult> LoadLibraryItemsAsync(
        AuthSession session,
        LibraryItem library,
        int startIndex,
        int limit,
        LibraryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(query);

        var contentResult = await GetWithFallbackAsync(
                session,
                BuildLibraryItemsPath(session.UserId, library, startIndex, limit, query),
                cancellationToken)
            .ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return LibraryItemsLoadResult.Failure(contentResult.Error);
        }

        var items = ParseItems(contentResult.Content!, contentResult.ApiBase!);
        return items is null
            ? LibraryItemsLoadResult.Failure(LibraryLoadError.InvalidResponse)
            : LibraryItemsLoadResult.Success(items.Items, items.TotalRecordCount);
    }

    private async Task<LibraryEndpointResult> GetWithFallbackAsync(
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

    private async Task<LibraryEndpointResult> GetStringAsync(
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
                return LibraryEndpointResult.NoFallback(LibraryLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return LibraryEndpointResult.NoFallback(LibraryLoadError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return LibraryEndpointResult.FallbackAllowed(LibraryLoadError.ServerError);
            }

            if (!response.IsSuccessStatusCode)
            {
                return LibraryEndpointResult.NoFallback(LibraryLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(content)
                ? LibraryEndpointResult.NoFallback(LibraryLoadError.InvalidResponse)
                : LibraryEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LibraryEndpointResult.NoFallback(LibraryLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return LibraryEndpointResult.NoFallback(LibraryLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return LibraryEndpointResult.NoFallback(LibraryLoadError.ServerUnreachable);
        }
    }

    private static IReadOnlyList<LibraryItem>? ParseLibraries(string content)
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
            return Array.Empty<LibraryItem>();
        }

        return response.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new LibraryItem(
                item.Id!,
                item.Name!,
                item.CollectionType ?? item.Type ?? string.Empty))
            .ToArray();
    }

    private static ParsedLibraryItems? ParseItems(string content, string apiBase)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ItemListResponse>(content, JsonOptions);
            if (response?.Items is null)
            {
                return new ParsedLibraryItems(Array.Empty<LibraryMediaItem>(), response?.TotalRecordCount);
            }

            var items = response.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new LibraryMediaItem(
                    item.Id!,
                    GetDisplayTitle(item),
                    item.Type ?? string.Empty,
                    item.ProductionYear,
                    BuildPosterUrl(apiBase, item),
                    GetPlayedPercentage(item),
                    item.UserData?.IsFavorite ?? false,
                    item.UserData?.Played ?? false))
                .ToArray();

            return new ParsedLibraryItems(items, response.TotalRecordCount);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildLibrariesPath(string userId)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Views";
    }

    private static string BuildLibraryItemsPath(
        string userId,
        LibraryItem library,
        int startIndex,
        int limit,
        LibraryQuery query)
    {
        var safeStartIndex = Math.Max(0, startIndex);
        var safeLimit = Math.Clamp(limit, 1, LibraryItemLimit);
        var queryParts = new List<string>
        {
            $"ParentId={Uri.EscapeDataString(library.Id)}",
            $"StartIndex={safeStartIndex}",
            $"Limit={safeLimit}",
            "Fields=UserData,PrimaryImageAspectRatio,ProductionYear,SeriesName,IndexNumber,ParentIndexNumber,RunTimeTicks",
            $"SortBy={query.SortField switch
            {
                LibrarySortField.DateAdded => "DateCreated,SortName",
                LibrarySortField.Year => "ProductionYear,SortName",
                _ => "SortName"
            }}",
            $"SortOrder={(query.SortDirection == LibrarySortDirection.Descending ? "Descending" : "Ascending")}",
            "EnableUserData=true"
        };

        if (query.WatchedFilter != LibraryWatchedFilter.All)
        {
            queryParts.Add($"IsPlayed={(query.WatchedFilter == LibraryWatchedFilter.Watched ? "true" : "false")}");
        }

        if (query.FavoritesOnly)
        {
            queryParts.Add("IsFavorite=true");
        }

        var includeItemTypes = GetIncludeItemTypes(library.Type);
        if (!string.IsNullOrWhiteSpace(includeItemTypes))
        {
            queryParts.Add("Recursive=true");
            queryParts.Add($"IncludeItemTypes={includeItemTypes}");
        }

        return $"/Users/{Uri.EscapeDataString(userId)}/Items?{string.Join("&", queryParts)}";
    }

    private static string? GetIncludeItemTypes(string libraryType)
    {
        return libraryType.Trim().ToLowerInvariant() switch
        {
            "movies" or "movie" => "Movie",
            "tvshows" or "tv" => "Series",
            "collections" or "boxsets" => "BoxSet",
            "playlists" => "Playlist",
            _ => null
        };
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

    private sealed record ParsedLibraryItems(
        IReadOnlyList<LibraryMediaItem> Items,
        int? TotalRecordCount);

    private sealed class LibraryEndpointResult
    {
        private LibraryEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            LibraryLoadError error,
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

        public LibraryLoadError Error { get; }

        public string? ApiBase { get; }

        public LibraryEndpointResult WithApiBase(string apiBase)
        {
            return new LibraryEndpointResult(IsSuccess, CanFallback, Content, Error, apiBase);
        }

        public static LibraryEndpointResult Success(string content)
        {
            return new LibraryEndpointResult(true, false, content, LibraryLoadError.None, null);
        }

        public static LibraryEndpointResult FallbackAllowed(LibraryLoadError error)
        {
            return new LibraryEndpointResult(false, true, null, error, null);
        }

        public static LibraryEndpointResult NoFallback(LibraryLoadError error)
        {
            return new LibraryEndpointResult(false, false, null, error, null);
        }
    }
}
