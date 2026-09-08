using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Devices;

namespace EmbyPlayer.Emby;

public sealed class EmbyMediaDetailService : IMediaDetailService
{
    private const int MaximumArtworkCount = 12;
    private const int MaximumPeopleCount = 24;
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

    public EmbyMediaDetailService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<MediaDetailLoadResult> LoadDetailAsync(
        AuthSession session,
        string itemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return MediaDetailLoadResult.Failure(MediaDetailLoadError.NotFound);
        }

        var contentResult = await GetWithFallbackAsync(
                session,
                BuildDetailPath(session.UserId, itemId),
                cancellationToken)
            .ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return MediaDetailLoadResult.Failure(contentResult.Error);
        }

        var detail = ParseDetail(contentResult.Content!, contentResult.ApiBase!);
        return detail is null
            ? MediaDetailLoadResult.Failure(MediaDetailLoadError.InvalidResponse)
            : MediaDetailLoadResult.Success(detail);
    }

    private async Task<MediaDetailEndpointResult> GetWithFallbackAsync(
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

    private async Task<MediaDetailEndpointResult> GetStringAsync(
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
                return MediaDetailEndpointResult.NoFallback(MediaDetailLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return MediaDetailEndpointResult.NoFallback(MediaDetailLoadError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return MediaDetailEndpointResult.FallbackAllowed(MediaDetailLoadError.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return MediaDetailEndpointResult.NoFallback(MediaDetailLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(content)
                ? MediaDetailEndpointResult.NoFallback(MediaDetailLoadError.InvalidResponse)
                : MediaDetailEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MediaDetailEndpointResult.NoFallback(MediaDetailLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return MediaDetailEndpointResult.NoFallback(MediaDetailLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return MediaDetailEndpointResult.NoFallback(MediaDetailLoadError.ServerUnreachable);
        }
    }

    private static MediaDetail? ParseDetail(string content, string apiBase)
    {
        ItemResponse? item;
        try
        {
            item = JsonSerializer.Deserialize<ItemResponse>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (item is null || string.IsNullOrWhiteSpace(item.Id))
        {
            return null;
        }

        return new MediaDetail(
            item.Id!,
            GetDisplayTitle(item),
            item.Type ?? string.Empty,
            item.ProductionYear,
            item.RunTimeTicks,
            item.Overview,
            item.Genres?.ToArray() ?? Array.Empty<string>(),
            item.CommunityRating,
            GetPlayedPercentage(item),
            item.UserData?.PlaybackPositionTicks,
            BuildPosterUrl(apiBase, item),
            BuildBackdropUrl(apiBase, item),
            item.UserData?.IsFavorite ?? false,
            item.UserData?.Played ?? false,
            BuildLogoUrl(apiBase, item),
            BuildPeople(apiBase, item),
            BuildArtworkUrls(apiBase, item));
    }

    private static string GetDisplayTitle(ItemResponse item)
    {
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.SeriesName))
        {
            return GetEpisodeDisplayTitle(item);
        }

        return string.IsNullOrWhiteSpace(item.Name) ? string.Empty : item.Name!;
    }

    private static string GetEpisodeDisplayTitle(ItemResponse item)
    {
        var name = NormalizeTitlePart(item.Name);
        var seriesName = NormalizeTitlePart(item.SeriesName);
        var episodeNumber = GetEpisodeNumberText(item);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var nameHasSeriesName = !string.IsNullOrWhiteSpace(seriesName)
                && name.Contains(seriesName, StringComparison.OrdinalIgnoreCase);
            if (nameHasSeriesName || HasEpisodeMarker(name))
            {
                return CleanExistingEpisodeTitle(name, seriesName, nameHasSeriesName);
            }
        }

        var prefix = string.IsNullOrWhiteSpace(episodeNumber)
            ? seriesName
            : $"{seriesName} {episodeNumber}";
        return string.IsNullOrWhiteSpace(name)
            ? prefix
            : $"{prefix} {name}";
    }

    private static string CleanExistingEpisodeTitle(
        string name,
        string seriesName,
        bool nameHasSeriesName)
    {
        var chineseEpisodeMatch = ChineseEpisodePattern.Match(name);
        if (nameHasSeriesName
            && chineseEpisodeMatch.Success
            && !string.IsNullOrWhiteSpace(seriesName))
        {
            return $"{seriesName} {NormalizeTitlePart(chineseEpisodeMatch.Value)}";
        }

        return name;
    }

    private static bool HasEpisodeMarker(string name)
    {
        return SeasonEpisodePattern.IsMatch(name) || ChineseEpisodePattern.IsMatch(name);
    }

    private static string NormalizeTitlePart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
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

    private static string? BuildPosterUrl(string apiBase, ItemResponse item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.ImageTags?.Primary))
        {
            return null;
        }

        return BuildEndpointUri(
                apiBase,
                $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Primary?maxHeight=700&quality=90")
            .ToString();
    }

    private static string? BuildBackdropUrl(string apiBase, ItemResponse item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)
            || item.BackdropImageTags is null
            || item.BackdropImageTags.Count == 0
            || string.IsNullOrWhiteSpace(item.BackdropImageTags[0]))
        {
            return null;
        }

        return BuildEndpointUri(
                apiBase,
                $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Backdrop/0?maxWidth=1600&quality=90")
            .ToString();
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

    private static IReadOnlyList<MediaPerson> BuildPeople(string apiBase, ItemResponse item)
    {
        if (item.People is null || item.People.Count == 0)
        {
            return Array.Empty<MediaPerson>();
        }

        return item.People
            .Where(static person => person is not null)
            .Take(MaximumPeopleCount)
            .Select(person => new MediaPerson(
                person!.Id ?? string.Empty,
                person.Name ?? string.Empty,
                person.Role ?? string.Empty,
                person.Type ?? string.Empty,
                BuildPersonImageUrl(apiBase, person)))
            .ToArray();
    }

    private static string? BuildPersonImageUrl(string apiBase, PersonResponse person)
    {
        if (string.IsNullOrWhiteSpace(person.Id)
            || string.IsNullOrWhiteSpace(person.PrimaryImageTag))
        {
            return null;
        }

        return BuildEndpointUri(
                apiBase,
                $"/Items/{Uri.EscapeDataString(person.Id)}/Images/Primary"
                + $"?tag={Uri.EscapeDataString(person.PrimaryImageTag)}&maxWidth=240&maxHeight=360&quality=85")
            .AbsoluteUri;
    }

    private static IReadOnlyList<string> BuildArtworkUrls(string apiBase, ItemResponse item)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            return Array.Empty<string>();
        }

        var urls = new List<string>(MaximumArtworkCount);
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        if (item.BackdropImageTags is not null)
        {
            for (var index = 0; index < item.BackdropImageTags.Count && urls.Count < MaximumArtworkCount; index++)
            {
                var tag = item.BackdropImageTags[index];
                if (string.IsNullOrWhiteSpace(tag) || !seenTags.Add(tag))
                {
                    continue;
                }

                urls.Add(BuildEndpointUri(
                        apiBase,
                        $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Backdrop/{index}"
                        + $"?tag={Uri.EscapeDataString(tag)}&maxWidth=720&quality=85")
                    .AbsoluteUri);
            }
        }

        var artTag = item.ImageTags?.Art;
        if (urls.Count < MaximumArtworkCount
            && !string.IsNullOrWhiteSpace(artTag)
            && seenTags.Add(artTag))
        {
            urls.Add(BuildEndpointUri(
                    apiBase,
                    $"/Items/{Uri.EscapeDataString(item.Id)}/Images/Art"
                    + $"?tag={Uri.EscapeDataString(artTag)}&maxWidth=720&quality=85")
                .AbsoluteUri);
        }

        return urls;
    }

    private static string BuildDetailPath(string userId, string itemId)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items/{Uri.EscapeDataString(itemId)}";
    }

    private static Uri BuildEndpointUri(string apiBase, string endpointPath)
    {
        return new Uri($"{apiBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private sealed class ItemResponse
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public int? ProductionYear { get; set; }

        public long? RunTimeTicks { get; set; }

        public string? Overview { get; set; }

        public List<string>? Genres { get; set; }

        public double? CommunityRating { get; set; }

        public string? SeriesName { get; set; }

        public int? IndexNumber { get; set; }

        public int? ParentIndexNumber { get; set; }

        public ItemImageTags? ImageTags { get; set; }

        public string? ParentLogoItemId { get; set; }

        public string? ParentLogoImageTag { get; set; }

        public List<string>? BackdropImageTags { get; set; }

        public List<PersonResponse?>? People { get; set; }

        public ItemUserData? UserData { get; set; }
    }

    private sealed class ItemImageTags
    {
        public string? Primary { get; set; }

        public string? Logo { get; set; }

        public string? Art { get; set; }
    }

    private sealed class PersonResponse
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Role { get; set; }

        public string? Type { get; set; }

        public string? PrimaryImageTag { get; set; }
    }

    private sealed class ItemUserData
    {
        public double? PlayedPercentage { get; set; }

        public long? PlaybackPositionTicks { get; set; }

        public bool? IsFavorite { get; set; }

        public bool? Played { get; set; }
    }

    private sealed class MediaDetailEndpointResult
    {
        private MediaDetailEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            MediaDetailLoadError error,
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

        public MediaDetailLoadError Error { get; }

        public string? ApiBase { get; }

        public MediaDetailEndpointResult WithApiBase(string apiBase)
        {
            return new MediaDetailEndpointResult(IsSuccess, CanFallback, Content, Error, apiBase);
        }

        public static MediaDetailEndpointResult Success(string content)
        {
            return new MediaDetailEndpointResult(true, false, content, MediaDetailLoadError.None, null);
        }

        public static MediaDetailEndpointResult FallbackAllowed(MediaDetailLoadError error)
        {
            return new MediaDetailEndpointResult(false, true, null, error, null);
        }

        public static MediaDetailEndpointResult NoFallback(MediaDetailLoadError error)
        {
            return new MediaDetailEndpointResult(false, false, null, error, null);
        }
    }
}
