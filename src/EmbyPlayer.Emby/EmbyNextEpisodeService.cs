using System.Net;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Series;

namespace EmbyPlayer.Emby;

public sealed class EmbyNextEpisodeService : INextEpisodeService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyNextEpisodeService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<NextEpisodeResult> GetNextEpisodeAsync(
        AuthSession session,
        string currentItemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(currentItemId))
        {
            return NextEpisodeResult.Success(null);
        }

        var currentResult = await GetWithFallbackAsync(
                session,
                BuildCurrentItemPath(session.UserId, currentItemId),
                cancellationToken)
            .ConfigureAwait(false);
        if (!currentResult.IsSuccess)
        {
            return NextEpisodeResult.Failure(currentResult.Error);
        }

        var current = DeserializeItem(currentResult.Content!);
        if (current is null)
        {
            return NextEpisodeResult.Failure(NextEpisodeError.InvalidResponse);
        }

        if (!string.Equals(current.Type, "Episode", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(current.SeriesId)
            || current.ParentIndexNumber is null
            || current.IndexNumber is null)
        {
            return NextEpisodeResult.Success(null);
        }

        var episodesResult = await GetWithFallbackAsync(
                session,
                BuildEpisodesPath(session.UserId, current.SeriesId),
                cancellationToken)
            .ConfigureAwait(false);
        if (!episodesResult.IsSuccess)
        {
            return NextEpisodeResult.Failure(episodesResult.Error);
        }

        var episodes = DeserializeItems(episodesResult.Content!);
        if (episodes is null)
        {
            return NextEpisodeResult.Failure(NextEpisodeError.InvalidResponse);
        }

        var ordered = episodes
            .Where(IsReliableEpisode)
            .OrderBy(episode => episode.ParentIndexNumber)
            .ThenBy(episode => episode.IndexNumber)
            .ThenBy(episode => episode.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentIndex = Array.FindIndex(
            ordered,
            episode => string.Equals(episode.Id, currentItemId, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0 || currentIndex >= ordered.Length - 1)
        {
            return NextEpisodeResult.Success(null);
        }

        var next = ordered[currentIndex + 1];
        return NextEpisodeResult.Success(new NextEpisodeInfo(
            next.Id!,
            GetTitle(next),
            next.ParentIndexNumber!.Value,
            next.IndexNumber!.Value,
            next.RunTimeTicks,
            BuildLogoUrl(episodesResult.ApiBase!, next),
            SeriesId: string.IsNullOrWhiteSpace(next.SeriesId) ? current.SeriesId : next.SeriesId,
            SeasonId: next.SeasonId));
    }

    private async Task<EndpointResult> GetWithFallbackAsync(
        AuthSession session,
        string endpointPath,
        CancellationToken cancellationToken)
    {
        var serverBase = session.ServerBase.TrimEnd('/');
        var primary = await GetStringAsync(
                session,
                new Uri($"{serverBase}{endpointPath}", UriKind.Absolute),
                cancellationToken)
            .ConfigureAwait(false);
        if (!primary.CanFallback)
        {
            return primary with { ApiBase = serverBase };
        }

        var embyApiBase = $"{serverBase}/emby";
        var fallback = await GetStringAsync(
                session,
                new Uri($"{embyApiBase}{endpointPath}", UriKind.Absolute),
                cancellationToken)
            .ConfigureAwait(false);
        return fallback with { ApiBase = embyApiBase };
    }

    private async Task<EndpointResult> GetStringAsync(
        AuthSession session,
        Uri endpointUri,
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
            request.Headers.TryAddWithoutValidation("X-Emby-Token", session.AccessToken);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return EndpointResult.NoFallback(NextEpisodeError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return EndpointResult.NoFallback(NextEpisodeError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return EndpointResult.FallbackAllowed(NextEpisodeError.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return EndpointResult.NoFallback(NextEpisodeError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content)
                ? EndpointResult.NoFallback(NextEpisodeError.InvalidResponse)
                : EndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EndpointResult.NoFallback(NextEpisodeError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return EndpointResult.NoFallback(NextEpisodeError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return EndpointResult.NoFallback(NextEpisodeError.ServerUnreachable);
        }
    }

    private static ItemResponse? DeserializeItem(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<ItemResponse>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ItemResponse>? DeserializeItems(string content)
    {
        try
        {
            var items = JsonSerializer.Deserialize<ItemListResponse>(content, JsonOptions)?.Items;
            return items is null ? Array.Empty<ItemResponse>() : items;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsReliableEpisode(ItemResponse episode)
    {
        return !string.IsNullOrWhiteSpace(episode.Id)
            && episode.ParentIndexNumber is not null
            && episode.IndexNumber is not null;
    }

    private static string GetTitle(ItemResponse episode)
    {
        return string.IsNullOrWhiteSpace(episode.Name)
            ? $"第 {episode.IndexNumber!.Value} 集"
            : episode.Name.Trim();
    }

    private static string? BuildLogoUrl(string apiBase, ItemResponse item)
    {
        var (logoItemId, logoTag) = !string.IsNullOrWhiteSpace(item.ImageTags?.Logo)
            ? (item.Id, item.ImageTags.Logo)
            : (item.ParentLogoItemId, item.ParentLogoImageTag);
        return string.IsNullOrWhiteSpace(logoItemId) || string.IsNullOrWhiteSpace(logoTag)
            ? null
            : $"{apiBase.TrimEnd('/')}/Items/{Uri.EscapeDataString(logoItemId)}/Images/Logo"
                + $"?tag={Uri.EscapeDataString(logoTag)}";
    }

    private static string BuildCurrentItemPath(string userId, string itemId)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items/{Uri.EscapeDataString(itemId)}"
            + "?Fields=SeriesId,SeasonId,ParentIndexNumber,IndexNumber,ImageTags,ParentLogoItemId,ParentLogoImageTag";
    }

    private static string BuildEpisodesPath(string userId, string seriesId)
    {
        return $"/Shows/{Uri.EscapeDataString(seriesId)}/Episodes"
            + $"?UserId={Uri.EscapeDataString(userId)}"
            + "&Fields=SeriesId,SeasonId,ParentIndexNumber,IndexNumber,RunTimeTicks,ImageTags,ParentLogoItemId,ParentLogoImageTag";
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

        public string? SeriesId { get; set; }

        public string? SeasonId { get; set; }

        public int? ParentIndexNumber { get; set; }

        public int? IndexNumber { get; set; }

        public long? RunTimeTicks { get; set; }

        public ItemImageTags? ImageTags { get; set; }

        public string? ParentLogoItemId { get; set; }

        public string? ParentLogoImageTag { get; set; }
    }

    private sealed class ItemImageTags
    {
        public string? Logo { get; set; }
    }

    private sealed record EndpointResult(
        bool IsSuccess,
        bool CanFallback,
        string? Content,
        NextEpisodeError Error,
        string? ApiBase = null)
    {
        public static EndpointResult Success(string content) => new(true, false, content, NextEpisodeError.None);

        public static EndpointResult FallbackAllowed(NextEpisodeError error) => new(false, true, null, error);

        public static EndpointResult NoFallback(NextEpisodeError error) => new(false, false, null, error);
    }
}
