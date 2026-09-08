using System.Net;
using System.Text.Json;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Servers;

namespace EmbyPlayer.Emby;

public sealed class EmbyServerConnectionService : IServerConnectionService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly IApplicationDiagnostics diagnostics;
    private readonly HttpClient httpClient;

    public EmbyServerConnectionService(
        HttpClient httpClient,
        IApplicationDiagnostics? diagnostics = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;
    }

    public async Task<ServerConnectionResult> ConnectAsync(string serverUrl, CancellationToken cancellationToken)
    {
        var normalizationResult = ServerUrlNormalizer.Normalize(serverUrl);
        if (!normalizationResult.IsSuccess || normalizationResult.Server is null)
        {
            return ConnectionFailure(normalizationResult.Error);
        }

        var server = normalizationResult.Server;
        var primaryResult = await ProbeAsync(
                BuildEndpointUri(server.ServerBase, "/System/Info/Public"),
                cancellationToken)
            .ConfigureAwait(false);

        if (primaryResult.IsSuccess)
        {
            return ServerConnectionResult.Success(server);
        }

        if (!primaryResult.CanFallback)
        {
            return ConnectionFailure(primaryResult.Error);
        }

        var fallbackResult = await ProbeAsync(
                BuildEndpointUri(server.ServerBase, "/emby/System/Info/Public"),
                cancellationToken)
            .ConfigureAwait(false);

        return fallbackResult.IsSuccess
            ? ServerConnectionResult.Success(server)
            : ConnectionFailure(fallbackResult.Error);
    }

    private async Task<ProbeResult> ProbeAsync(Uri endpointUri, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUri);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ProbeResult.FallbackAllowed(ToHttpFailureError(response.StatusCode));
            }

            var responseContent = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return ProbeResult.FallbackAllowed(ServerConnectionError.UnrecognizedServer);
            }

            try
            {
                using var _ = JsonDocument.Parse(responseContent);
                return ProbeResult.Success();
            }
            catch (JsonException)
            {
                return ProbeResult.FallbackAllowed(ServerConnectionError.UnrecognizedServer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.NoFallback(ServerConnectionError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.NoFallback(ServerConnectionError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return ProbeResult.NoFallback(ServerConnectionError.ServerUnreachable);
        }
    }

    private static Uri BuildEndpointUri(string serverBase, string endpointPath)
    {
        return new Uri($"{serverBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private ServerConnectionResult ConnectionFailure(ServerConnectionError error)
    {
        if (error != ServerConnectionError.Cancelled)
        {
            diagnostics.Write("connection", "failed", $"reason={error}");
        }

        return ServerConnectionResult.Failure(error);
    }

    private static ServerConnectionError ToHttpFailureError(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ServerConnectionError.UnrecognizedServer
            : ServerConnectionError.ServerUnreachable;
    }

    private sealed class ProbeResult
    {
        private ProbeResult(bool isSuccess, bool canFallback, ServerConnectionError error)
        {
            IsSuccess = isSuccess;
            CanFallback = canFallback;
            Error = error;
        }

        public bool IsSuccess { get; }

        public bool CanFallback { get; }

        public ServerConnectionError Error { get; }

        public static ProbeResult Success()
        {
            return new ProbeResult(true, false, ServerConnectionError.None);
        }

        public static ProbeResult FallbackAllowed(ServerConnectionError error)
        {
            return new ProbeResult(false, true, error);
        }

        public static ProbeResult NoFallback(ServerConnectionError error)
        {
            return new ProbeResult(false, false, error);
        }
    }
}
