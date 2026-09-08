using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Series;

namespace EmbyPlayer.Emby;

public sealed class EmbySeriesService : ISeriesService
{
    private static readonly Regex SeasonEpisodePattern = new(
        @"\bS\d{1,3}\s*[:E]\s*\d{1,5}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex ChineseEpisodePattern = new(
        @"第\s*[0-9一二三四五六七八九十百千万零〇两]+\s*集",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbySeriesService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<SeriesSeasonsLoadResult> LoadSeasonsAsync(
        AuthSession session,
        string seriesId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return SeriesSeasonsLoadResult.Failure(SeriesLoadError.NotFound);
        }

        var contentResult = await GetWithFallbackAsync(
                session,
                BuildSeasonsPath(session.UserId, seriesId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return SeriesSeasonsLoadResult.Failure(contentResult.Error);
        }

        var seasons = ParseSeasons(contentResult.Content!);
        return seasons is null
            ? SeriesSeasonsLoadResult.Failure(SeriesLoadError.InvalidResponse)
            : SeriesSeasonsLoadResult.Success(seasons);
    }

    public async Task<SeriesEpisodesLoadResult> LoadEpisodesAsync(
        AuthSession session,
        string seriesId,
        string seasonId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(seriesId) || string.IsNullOrWhiteSpace(seasonId))
        {
            return SeriesEpisodesLoadResult.Failure(SeriesLoadError.NotFound);
        }

        var contentResult = await GetWithFallbackAsync(
                session,
                BuildEpisodesPath(session.UserId, seriesId, seasonId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return SeriesEpisodesLoadResult.Failure(contentResult.Error);
        }

        var episodes = ParseEpisodes(contentResult.Content!, contentResult.ApiBase!);
        return episodes is null
            ? SeriesEpisodesLoadResult.Failure(SeriesLoadError.InvalidResponse)
            : SeriesEpisodesLoadResult.Success(episodes);
    }

    private async Task<SeriesEndpointResult> GetWithFallbackAsync(
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

    private async Task<SeriesEndpointResult> GetStringAsync(
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
                return SeriesEndpointResult.NoFallback(SeriesLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return SeriesEndpointResult.NoFallback(SeriesLoadError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return SeriesEndpointResult.FallbackAllowed(SeriesLoadError.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return SeriesEndpointResult.NoFallback(SeriesLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(content)
                ? SeriesEndpointResult.NoFallback(SeriesLoadError.InvalidResponse)
                : SeriesEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SeriesEndpointResult.NoFallback(SeriesLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return SeriesEndpointResult.NoFallback(SeriesLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return SeriesEndpointResult.NoFallback(SeriesLoadError.ServerUnreachable);
        }
    }

    private static IReadOnlyList<SeasonInfo>? ParseSeasons(string content)
    {
        try
        {
            var items = DeserializeItems(content);
            if (items is null)
            {
                return Array.Empty<SeasonInfo>();
            }

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new SeasonInfo(
                    item.Id!,
                    GetSeasonName(item),
                    item.IndexNumber,
                    HasResumeOrUnwatched(item)))
                .OrderBy(season => season.IndexNumber ?? int.MaxValue)
                .ThenBy(season => season.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<EpisodeInfo>? ParseEpisodes(string content, string apiBase)
    {
        try
        {
            var items = DeserializeItems(content);
            if (items is null)
            {
                return Array.Empty<EpisodeInfo>();
            }

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new EpisodeInfo(
                    item.Id!,
                    GetEpisodeTitle(item),
                    item.ParentIndexNumber,
                    item.IndexNumber,
                    item.RunTimeTicks,
                    item.Overview,
                    GetPlayedPercentage(item),
                    item.UserData?.PlaybackPositionTicks,
                    BuildThumbnailUrl(apiBase, item),
                    item.UserData?.Played == true,
                    BuildHeroImageUrl(apiBase, item)))
                .OrderBy(episode => episode.SeasonIndex ?? int.MaxValue)
                .ThenBy(episode => episode.EpisodeIndex ?? int.MaxValue)
                .ThenBy(episode => episode.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ItemResponse>? DeserializeItems(string content)
    {
        var response = JsonSerializer.Deserialize<ItemListResponse>(content, JsonOptions);
        return response?.Items;
    }

    private static string GetSeasonName(ItemResponse item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            return item.Name!;
        }

        return item.IndexNumber is null
            ? "未命名季"
            : $"第 {item.IndexNumber.Value} 季";
    }

    private static bool HasResumeOrUnwatched(ItemResponse item)
    {
        return item.UserData?.PlaybackPositionTicks is > 0
            || item.UserData?.PlayedPercentage is > 0 and < 100
            || item.UserData?.UnplayedItemCount is > 0
            || item.UnplayedItemCount is > 0;
    }

    private static string GetEpisodeTitle(ItemResponse item)
    {
        var episodeNumber = item.IndexNumber is null ? string.Empty : $"第 {item.IndexNumber.Value} 集";
        var name = NormalizeTitlePart(item.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.IsNullOrWhiteSpace(episodeNumber) ? "未命名单集" : episodeNumber;
        }

        var cleanedName = RemoveEpisodeMarkers(RemoveSeriesName(name, item.SeriesName));
        if (string.IsNullOrWhiteSpace(cleanedName))
        {
            return string.IsNullOrWhiteSpace(episodeNumber) ? name : episodeNumber;
        }

        return string.IsNullOrWhiteSpace(episodeNumber)
            ? cleanedName
            : $"{episodeNumber}：{cleanedName}";
    }

    private static string RemoveSeriesName(string name, string? seriesName)
    {
        var normalizedSeriesName = NormalizeTitlePart(seriesName);
        if (string.IsNullOrWhiteSpace(normalizedSeriesName))
        {
            return name;
        }

        return name.Replace(normalizedSeriesName, string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveEpisodeMarkers(string name)
    {
        var withoutSeasonEpisode = SeasonEpisodePattern.Replace(name, string.Empty);
        var withoutChineseEpisode = ChineseEpisodePattern.Replace(withoutSeasonEpisode, string.Empty);
        return NormalizeTitlePart(withoutChineseEpisode.Trim(' ', '-', '—', '_', ':', '：'));
    }

    private static string NormalizeTitlePart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
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

    private static string? BuildThumbnailUrl(string apiBase, ItemResponse item)
    {
        return BuildEpisodePrimaryImageUrl(apiBase, item, 480);
    }

    private static string? BuildHeroImageUrl(string apiBase, ItemResponse item)
    {
        return BuildEpisodePrimaryImageUrl(apiBase, item, 1600);
    }

    private static string? BuildEpisodePrimaryImageUrl(string apiBase, ItemResponse item, int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.ImageTags?.Primary))
        {
            return null;
        }

        return BuildEndpointUri(
                apiBase,
                $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Primary"
                + $"?tag={Uri.EscapeDataString(item.ImageTags.Primary)}&maxWidth={maxWidth}&quality=90")
            .AbsoluteUri;
    }

    private static string BuildSeasonsPath(string userId, string seriesId)
    {
        return $"/Shows/{Uri.EscapeDataString(seriesId)}/Seasons?UserId={Uri.EscapeDataString(userId)}";
    }

    private static string BuildEpisodesPath(string userId, string seriesId, string seasonId)
    {
        return $"/Shows/{Uri.EscapeDataString(seriesId)}/Episodes"
            + $"?UserId={Uri.EscapeDataString(userId)}"
            + $"&SeasonId={Uri.EscapeDataString(seasonId)}"
            + "&EnableUserData=true"
            + "&Fields=UserData,PrimaryImageAspectRatio,Overview,RunTimeTicks,SeriesName,IndexNumber,ParentIndexNumber,ImageTags";
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

        public int? IndexNumber { get; set; }

        public int? ParentIndexNumber { get; set; }

        public string? SeriesName { get; set; }

        public long? RunTimeTicks { get; set; }

        public string? Overview { get; set; }

        public int? UnplayedItemCount { get; set; }

        public ItemImageTags? ImageTags { get; set; }

        public ItemUserData? UserData { get; set; }
    }

    private sealed class ItemImageTags
    {
        public string? Primary { get; set; }
    }

    private sealed class ItemUserData
    {
        public bool Played { get; set; }

        public double? PlayedPercentage { get; set; }

        public long? PlaybackPositionTicks { get; set; }

        public int? UnplayedItemCount { get; set; }
    }

    private sealed class SeriesEndpointResult
    {
        private SeriesEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            SeriesLoadError error,
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

        public SeriesLoadError Error { get; }

        public string? ApiBase { get; }

        public SeriesEndpointResult WithApiBase(string apiBase)
        {
            return new SeriesEndpointResult(IsSuccess, CanFallback, Content, Error, apiBase);
        }

        public static SeriesEndpointResult Success(string content)
        {
            return new SeriesEndpointResult(true, false, content, SeriesLoadError.None, null);
        }

        public static SeriesEndpointResult FallbackAllowed(SeriesLoadError error)
        {
            return new SeriesEndpointResult(false, true, null, error, null);
        }

        public static SeriesEndpointResult NoFallback(SeriesLoadError error)
        {
            return new SeriesEndpointResult(false, false, null, error, null);
        }
    }
}
