using System.Net;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;

namespace EmbyPlayer.Emby;

public sealed class EmbyAuthSessionValidator : IAuthSessionValidator
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyAuthSessionValidator(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<AuthSessionValidationResult> ValidateAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);

        var primaryResult = await ValidateEndpointAsync(
                BuildEndpointUri(session.ServerBase, "/System/Ping"),
                session.AccessToken,
                deviceId,
                cancellationToken)
            .ConfigureAwait(false);

        if (primaryResult.Status == AuthSessionValidationStatus.Valid
            || primaryResult.Status == AuthSessionValidationStatus.InvalidToken
            || primaryResult.Status == AuthSessionValidationStatus.Forbidden
            || !primaryResult.CanFallback)
        {
            return primaryResult.ToValidationResult();
        }

        var fallbackResult = await ValidateEndpointAsync(
                BuildEndpointUri(session.ServerBase, "/emby/System/Ping"),
                session.AccessToken,
                deviceId,
                cancellationToken)
            .ConfigureAwait(false);

        return fallbackResult.ToValidationResult();
    }

    private async Task<PingEndpointResult> ValidateEndpointAsync(
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

            if (response.IsSuccessStatusCode)
            {
                return PingEndpointResult.NoFallback(AuthSessionValidationStatus.Valid);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return PingEndpointResult.NoFallback(AuthSessionValidationStatus.InvalidToken);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return PingEndpointResult.NoFallback(AuthSessionValidationStatus.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return PingEndpointResult.FallbackAllowed(AuthSessionValidationStatus.ValidationFailed);
            }

            return PingEndpointResult.NoFallback(AuthSessionValidationStatus.ValidationFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PingEndpointResult.NoFallback(AuthSessionValidationStatus.NetworkUnavailable);
        }
        catch (OperationCanceledException)
        {
            return PingEndpointResult.NoFallback(AuthSessionValidationStatus.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return PingEndpointResult.NoFallback(AuthSessionValidationStatus.NetworkUnavailable);
        }
    }

    private static Uri BuildEndpointUri(string serverBase, string endpointPath)
    {
        return new Uri($"{serverBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private sealed class PingEndpointResult
    {
        private PingEndpointResult(AuthSessionValidationStatus status, bool canFallback)
        {
            Status = status;
            CanFallback = canFallback;
        }

        public AuthSessionValidationStatus Status { get; }

        public bool CanFallback { get; }

        public AuthSessionValidationResult ToValidationResult()
        {
            return Status switch
            {
                AuthSessionValidationStatus.Valid => AuthSessionValidationResult.Valid(),
                AuthSessionValidationStatus.InvalidToken => AuthSessionValidationResult.InvalidToken(),
                AuthSessionValidationStatus.Forbidden => AuthSessionValidationResult.Forbidden(),
                AuthSessionValidationStatus.NetworkUnavailable => AuthSessionValidationResult.NetworkUnavailable(),
                AuthSessionValidationStatus.ServerTimeout => AuthSessionValidationResult.ServerTimeout(),
                _ => AuthSessionValidationResult.ValidationFailed()
            };
        }

        public static PingEndpointResult NoFallback(AuthSessionValidationStatus status)
        {
            return new PingEndpointResult(status, false);
        }

        public static PingEndpointResult FallbackAllowed(AuthSessionValidationStatus status)
        {
            return new PingEndpointResult(status, true);
        }
    }
}
