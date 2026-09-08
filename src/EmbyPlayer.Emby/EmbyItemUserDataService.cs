using System.Net;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.UserData;

namespace EmbyPlayer.Emby;

public sealed class EmbyItemUserDataService : IItemUserDataService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyItemUserDataService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public Task<ItemUserDataResult> SetFavoriteAsync(
        AuthSession session,
        string itemId,
        bool isFavorite,
        CancellationToken cancellationToken)
    {
        return SendUserDataCommandAsync(
            session,
            itemId,
            isFavorite,
            "FavoriteItems",
            cancellationToken);
    }

    public Task<ItemUserDataResult> SetPlayedAsync(
        AuthSession session,
        string itemId,
        bool isPlayed,
        CancellationToken cancellationToken)
    {
        return SendUserDataCommandAsync(
            session,
            itemId,
            isPlayed,
            "PlayedItems",
            cancellationToken);
    }

    private Task<ItemUserDataResult> SendUserDataCommandAsync(
        AuthSession session,
        string itemId,
        bool enabled,
        string endpointName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Task.FromResult(ItemUserDataResult.Failure(ItemUserDataError.NotFound));
        }

        var method = enabled ? HttpMethod.Post : HttpMethod.Delete;
        var endpointPath = BuildUserDataPath(session.UserId, endpointName, itemId);
        return SendWithFallbackAsync(session, method, endpointPath, cancellationToken);
    }

    private async Task<ItemUserDataResult> SendWithFallbackAsync(
        AuthSession session,
        HttpMethod method,
        string endpointPath,
        CancellationToken cancellationToken)
    {
        var serverBase = session.ServerBase.TrimEnd('/');
        var primaryResult = await SendAsync(
                BuildEndpointUri(serverBase, endpointPath),
                method,
                session.AccessToken,
                cancellationToken)
            .ConfigureAwait(false);

        if (!primaryResult.CanFallback)
        {
            return primaryResult.ToResult();
        }

        var fallbackResult = await SendAsync(
                BuildEndpointUri($"{serverBase}/emby", endpointPath),
                method,
                session.AccessToken,
                cancellationToken)
            .ConfigureAwait(false);

        return fallbackResult.ToResult();
    }

    private async Task<ItemUserDataEndpointResult> SendAsync(
        Uri endpointUri,
        HttpMethod method,
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
            using var request = new HttpRequestMessage(method, endpointUri);
            request.Headers.TryAddWithoutValidation("X-Emby-Token", accessToken);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ItemUserDataEndpointResult.NoFallback(ItemUserDataError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return ItemUserDataEndpointResult.NoFallback(ItemUserDataError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return ItemUserDataEndpointResult.FallbackAllowed(ItemUserDataError.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ItemUserDataEndpointResult.NoFallback(ItemUserDataError.ServerError);
            }

            return ItemUserDataEndpointResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ItemUserDataEndpointResult.NoFallback(ItemUserDataError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return ItemUserDataEndpointResult.NoFallback(ItemUserDataError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return ItemUserDataEndpointResult.NoFallback(ItemUserDataError.ServerUnreachable);
        }
    }

    private static string BuildUserDataPath(string userId, string endpointName, string itemId)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/{endpointName}/{Uri.EscapeDataString(itemId)}";
    }

    private static Uri BuildEndpointUri(string apiBase, string endpointPath)
    {
        return new Uri($"{apiBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private sealed class ItemUserDataEndpointResult
    {
        private ItemUserDataEndpointResult(bool isSuccess, bool canFallback, ItemUserDataError error)
        {
            IsSuccess = isSuccess;
            CanFallback = canFallback;
            Error = error;
        }

        public bool IsSuccess { get; }

        public bool CanFallback { get; }

        public ItemUserDataError Error { get; }

        public ItemUserDataResult ToResult()
        {
            return IsSuccess
                ? ItemUserDataResult.Success()
                : ItemUserDataResult.Failure(Error);
        }

        public static ItemUserDataEndpointResult Success()
        {
            return new ItemUserDataEndpointResult(true, false, ItemUserDataError.None);
        }

        public static ItemUserDataEndpointResult FallbackAllowed(ItemUserDataError error)
        {
            return new ItemUserDataEndpointResult(false, true, error);
        }

        public static ItemUserDataEndpointResult NoFallback(ItemUserDataError error)
        {
            return new ItemUserDataEndpointResult(false, false, error);
        }
    }
}
