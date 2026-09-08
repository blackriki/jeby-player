using System.Net;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.People;

namespace EmbyPlayer.Emby;

public sealed class EmbyPersonService(HttpClient httpClient, IDeviceIdService deviceIdService) : IPersonService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IDeviceIdService deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));

    public async Task<PersonLoadResult> LoadPersonAsync(AuthSession session, string personId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(personId)) return PersonLoadResult.Failure(PersonLoadError.NotFound);
        var result = await GetAsync(session,
            $"/Users/{Uri.EscapeDataString(session.UserId)}/Items/{Uri.EscapeDataString(personId)}", cancellationToken)
            .ConfigureAwait(false);
        if (result.Error != PersonLoadError.None) return PersonLoadResult.Failure(result.Error);
        try
        {
            var item = JsonSerializer.Deserialize<ItemResponse>(result.Content!, JsonOptions);
            return item is null || string.IsNullOrWhiteSpace(item.Id)
                ? PersonLoadResult.Failure(PersonLoadError.InvalidResponse)
                : PersonLoadResult.Success(new PersonDetail(item.Id, item.Name ?? string.Empty,
                    item.Overview, BuildImageUrl(result.ApiBase!, item, 480)));
        }
        catch (JsonException)
        {
            return PersonLoadResult.Failure(PersonLoadError.InvalidResponse);
        }
    }

    public async Task<PersonWorksLoadResult> LoadWorksAsync(
        AuthSession session, string personId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(personId)) return PersonWorksLoadResult.Failure(PersonLoadError.NotFound);
        var safeStart = Math.Max(0, startIndex);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var path = $"/Users/{Uri.EscapeDataString(session.UserId)}/Items"
            + $"?PersonIds={Uri.EscapeDataString(personId)}&Recursive=true&IncludeItemTypes=Movie,Series"
            + $"&StartIndex={safeStart}&Limit={safeLimit}&SortBy=ProductionYear,SortName&SortOrder=Descending"
            + "&Fields=ProductionYear,UserData,RunTimeTicks,ImageTags&EnableUserData=true"
            + "&EnableImages=true&ImageTypeLimit=1&EnableImageTypes=Primary";
        var result = await GetAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (result.Error != PersonLoadError.None) return PersonWorksLoadResult.Failure(result.Error);
        try
        {
            var response = JsonSerializer.Deserialize<ItemListResponse>(result.Content!, JsonOptions);
            if (response is null) return PersonWorksLoadResult.Failure(PersonLoadError.InvalidResponse);
            var rows = response.Items ?? new List<ItemResponse>();
            var items = rows.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id)
                    && (string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.Type, "Series", StringComparison.OrdinalIgnoreCase)))
                .Select(item => new LibraryMediaItem(item.Id!, item.Name ?? string.Empty, item.Type!,
                    item.ProductionYear, BuildImageUrl(result.ApiBase!, item, 360), GetProgress(item),
                    item.UserData?.IsFavorite ?? false, item.UserData?.Played ?? false)).ToArray();
            var nextStart = safeStart + rows.Count;
            return PersonWorksLoadResult.Success(items, nextStart, rows.Count > 0
                && (response.TotalRecordCount is int total ? nextStart < total : rows.Count >= safeLimit));
        }
        catch (JsonException)
        {
            return PersonWorksLoadResult.Failure(PersonLoadError.InvalidResponse);
        }
    }

    private async Task<EndpointResult> GetAsync(AuthSession session, string path, CancellationToken cancellationToken)
    {
        try
        {
            var deviceId = await deviceIdService.GetOrCreateDeviceIdAsync(cancellationToken).ConfigureAwait(false);
            var apiBase = session.ServerBase.TrimEnd('/');
            var result = await SendAsync(apiBase, path, session.AccessToken, deviceId, cancellationToken).ConfigureAwait(false);
            return result.CanFallback
                ? await SendAsync(apiBase + "/emby", path, session.AccessToken, deviceId, cancellationToken).ConfigureAwait(false)
                : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EndpointResult(PersonLoadError.Cancelled);
        }
    }

    private async Task<EndpointResult> SendAsync(
        string apiBase, string path, string token, string deviceId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiBase + path);
            request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
            request.Headers.TryAddWithoutValidation("X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new EndpointResult(PersonLoadError.Unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new EndpointResult(PersonLoadError.Forbidden);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                return new EndpointResult(PersonLoadError.NotFound, CanFallback: true);
            if (!response.IsSuccessStatusCode) return new EndpointResult(PersonLoadError.ServerError);
            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content)
                ? new EndpointResult(PersonLoadError.InvalidResponse)
                : new EndpointResult(PersonLoadError.None, content, apiBase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EndpointResult(PersonLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return new EndpointResult(PersonLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return new EndpointResult(PersonLoadError.ServerUnreachable);
        }
    }

    private static string? BuildImageUrl(string apiBase, ItemResponse item, int height)
        => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ImageTags?.Primary) ? null
            : $"{apiBase}/Items/{Uri.EscapeDataString(item.Id)}/Images/Primary"
                + $"?tag={Uri.EscapeDataString(item.ImageTags.Primary)}&maxHeight={height}&quality=85";

    private static double? GetProgress(ItemResponse item)
        => item.UserData?.PlayedPercentage is > 0 and < 100 ? item.UserData.PlayedPercentage
            : item.UserData?.PlaybackPositionTicks is > 0 && item.RunTimeTicks is > 0
                ? Math.Clamp(item.UserData.PlaybackPositionTicks.Value * 100d / item.RunTimeTicks.Value, 0, 100) : null;

    private sealed record EndpointResult(PersonLoadError Error, string? Content = null, string? ApiBase = null, bool CanFallback = false);
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
        public string? Overview { get; set; }
        public int? ProductionYear { get; set; }
        public long? RunTimeTicks { get; set; }
        public ImageTags? ImageTags { get; set; }
        public UserData? UserData { get; set; }
    }
    private sealed class ImageTags { public string? Primary { get; set; } }
    private sealed class UserData
    {
        public double? PlayedPercentage { get; set; }
        public long? PlaybackPositionTicks { get; set; }
        public bool? IsFavorite { get; set; }
        public bool? Played { get; set; }
    }
}
