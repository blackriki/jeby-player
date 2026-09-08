using System.Diagnostics;
using System.Net;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Library;

namespace EmbyPlayer.Emby;

public sealed class EmbyMediaLibraryScanService : IMediaLibraryScanService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyMediaLibraryScanService(HttpClient httpClient, IDeviceIdService deviceIdService)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
    }

    public async Task<MediaLibraryScanResult> RequestScanAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var serverBase = session.ServerBase.TrimEnd('/');
        var result = await SendAsync(
                new Uri($"{serverBase}/Library/Refresh", UriKind.Absolute),
                session,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.CanFallback || serverBase.EndsWith("/emby", StringComparison.OrdinalIgnoreCase))
        {
            return result.Result;
        }

        return (await SendAsync(
                new Uri($"{serverBase}/emby/Library/Refresh", UriKind.Absolute),
                session,
                cancellationToken)
            .ConfigureAwait(false)).Result;
    }

    private async Task<(MediaLibraryScanResult Result, bool CanFallback)> SendAsync(
        Uri endpointUri,
        AuthSession session,
        CancellationToken cancellationToken)
    {
        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
            request.Headers.TryAddWithoutValidation("X-Emby-Token", session.AccessToken);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.Unauthorized), false);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return (MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.Forbidden), false);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return (MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.ServerError), true);
            }

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"Media library scan request failed with HTTP {(int)response.StatusCode}.");
                return (MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.ServerError), false);
            }

            return (MediaLibraryScanResult.Success(), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.Cancelled), false);
        }
        catch (OperationCanceledException)
        {
            return (MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.ServerTimeout), false);
        }
        catch (HttpRequestException exception)
        {
            Debug.WriteLine($"Media library scan request failed: {exception.Message}");
            return (MediaLibraryScanResult.Failure(MediaLibraryScanResult.FailureReason.ServerUnreachable), false);
        }
    }
}
