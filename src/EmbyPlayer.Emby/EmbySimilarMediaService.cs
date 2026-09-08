using System.Net;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Devices;

namespace EmbyPlayer.Emby;

public sealed class EmbySimilarMediaService : ISimilarMediaService
{
    private const int SimilarItemLimit = 12;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbySimilarMediaService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<SimilarMediaLoadResult> LoadSimilarAsync(
        AuthSession session,
        string itemId,
        string itemType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return SimilarMediaLoadResult.Failure(SimilarMediaLoadError.NotFound);
        }

        var similarItemType = string.Equals(itemType, "Movie", StringComparison.OrdinalIgnoreCase)
            ? "Movie"
            : string.Equals(itemType, "Series", StringComparison.OrdinalIgnoreCase) ? "Series" : null;
        if (similarItemType is null)
        {
            return SimilarMediaLoadResult.Success(Array.Empty<SimilarMediaItem>());
        }

        var contentResult = await GetWithFallbackAsync(
                session,
                BuildSimilarPath(session.UserId, itemId, similarItemType),
                cancellationToken)
            .ConfigureAwait(false);
        if (!contentResult.IsSuccess)
        {
            return SimilarMediaLoadResult.Failure(contentResult.Error);
        }

        var items = ParseItems(contentResult.Content!, contentResult.ApiBase!, itemId, similarItemType);
        return items is null
            ? SimilarMediaLoadResult.Failure(SimilarMediaLoadError.InvalidResponse)
            : SimilarMediaLoadResult.Success(items);
    }

    private async Task<SimilarEndpointResult> GetWithFallbackAsync(
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

    private async Task<SimilarEndpointResult> GetStringAsync(
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
                return SimilarEndpointResult.NoFallback(SimilarMediaLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return SimilarEndpointResult.NoFallback(SimilarMediaLoadError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return SimilarEndpointResult.FallbackAllowed(SimilarMediaLoadError.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return SimilarEndpointResult.NoFallback(SimilarMediaLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content)
                ? SimilarEndpointResult.NoFallback(SimilarMediaLoadError.InvalidResponse)
                : SimilarEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SimilarEndpointResult.NoFallback(SimilarMediaLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return SimilarEndpointResult.NoFallback(SimilarMediaLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return SimilarEndpointResult.NoFallback(SimilarMediaLoadError.ServerUnreachable);
        }
    }

    private static IReadOnlyList<SimilarMediaItem>? ParseItems(
        string content,
        string apiBase,
        string currentItemId,
        string itemType)
    {
        ItemListResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ItemListResponse>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (response is null)
        {
            return null;
        }

        return (response.Items ?? new List<ItemResponse>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                && !string.Equals(item.Id, currentItemId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Type, itemType, StringComparison.OrdinalIgnoreCase))
            .Take(SimilarItemLimit)
            .Select(item => new SimilarMediaItem(
                item.Id!,
                item.Name ?? string.Empty,
                item.Type ?? string.Empty,
                item.ProductionYear,
                BuildPosterUrl(apiBase, item),
                GetPlayedPercentage(item),
                item.UserData?.PlaybackPositionTicks,
                item.UserData?.Played ?? false))
            .ToArray();
    }

    private static double? GetPlayedPercentage(ItemResponse item)
    {
        if (item.UserData?.PlayedPercentage is > 0 and < 100)
        {
            return item.UserData.PlayedPercentage;
        }

        if (item.UserData?.PlaybackPositionTicks is > 0 && item.RunTimeTicks is > 0)
        {
            return Math.Clamp(
                item.UserData.PlaybackPositionTicks.Value * 100d / item.RunTimeTicks.Value,
                0d,
                100d);
        }

        return null;
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
                $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Primary"
                + $"?tag={Uri.EscapeDataString(item.ImageTags.Primary)}&maxHeight=360&quality=90")
            .AbsoluteUri;
    }

    private static string BuildSimilarPath(string userId, string itemId, string itemType)
    {
        return $"/Items/{Uri.EscapeDataString(itemId)}/Similar"
            + $"?UserId={Uri.EscapeDataString(userId)}"
            + $"&Limit={SimilarItemLimit}"
            + "&EnableImages=true"
            + "&ImageTypeLimit=1"
            + "&EnableImageTypes=Primary"
            + "&EnableUserData=true"
            + $"&IncludeItemTypes={itemType}";
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

        public bool? Played { get; set; }
    }

    private sealed class SimilarEndpointResult
    {
        private SimilarEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            SimilarMediaLoadError error,
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

        public SimilarMediaLoadError Error { get; }

        public string? ApiBase { get; }

        public SimilarEndpointResult WithApiBase(string apiBase)
        {
            return new SimilarEndpointResult(IsSuccess, CanFallback, Content, Error, apiBase);
        }

        public static SimilarEndpointResult Success(string content)
        {
            return new SimilarEndpointResult(true, false, content, SimilarMediaLoadError.None, null);
        }

        public static SimilarEndpointResult FallbackAllowed(SimilarMediaLoadError error)
        {
            return new SimilarEndpointResult(false, true, null, error, null);
        }

        public static SimilarEndpointResult NoFallback(SimilarMediaLoadError error)
        {
            return new SimilarEndpointResult(false, false, null, error, null);
        }
    }
}
