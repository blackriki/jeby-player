using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Servers;

namespace EmbyPlayer.Emby;

public sealed class EmbyAuthenticationService : IAuthenticationService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IApplicationDiagnostics diagnostics;
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyAuthenticationService(
        HttpClient httpClient,
        IDeviceIdService deviceIdService,
        IApplicationDiagnostics? diagnostics = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string serverBase,
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizationResult = ServerUrlNormalizer.Normalize(serverBase);
        if (!normalizationResult.IsSuccess || normalizationResult.Server is null)
        {
            return AuthenticationFailure(AuthenticationError.MissingServer);
        }

        var normalizedServerBase = normalizationResult.Server.ServerBase;
        string deviceId;
        try
        {
            deviceId = await deviceIdService
                .GetOrCreateDeviceIdAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthenticationFailure(AuthenticationError.Cancelled);
        }
        catch (Exception exception)
        {
            diagnostics.Write(
                "authentication",
                "failed",
                $"reason={AuthenticationError.LoginFailed} stage=device-id exceptionType={exception.GetType().Name}");
            return AuthenticationResult.Failure(AuthenticationError.LoginFailed);
        }

        var primaryResult = await AuthenticateEndpointAsync(
                normalizedServerBase,
                BuildEndpointUri(normalizedServerBase, "/Users/AuthenticateByName"),
                deviceId,
                userName,
                password,
                cancellationToken)
            .ConfigureAwait(false);

        if (primaryResult.IsSuccess)
        {
            return primaryResult.ToAuthenticationResult();
        }

        if (!primaryResult.CanFallback)
        {
            return AuthenticationFailure(primaryResult.Error);
        }

        var fallbackResult = await AuthenticateEndpointAsync(
                normalizedServerBase,
                BuildEndpointUri(normalizedServerBase, "/emby/Users/AuthenticateByName"),
                deviceId,
                userName,
                password,
                cancellationToken)
            .ConfigureAwait(false);

        return fallbackResult.IsSuccess
            ? fallbackResult.ToAuthenticationResult()
            : AuthenticationFailure(fallbackResult.Error);
    }

    private async Task<AuthEndpointResult> AuthenticateEndpointAsync(
        string serverBase,
        Uri endpointUri,
        string deviceId,
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");

            var requestBody = JsonSerializer.Serialize(new AuthenticateByNameRequest(userName, password), JsonOptions);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return AuthEndpointResult.NoFallback(AuthenticationError.InvalidCredentials);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return AuthEndpointResult.FallbackAllowed(AuthenticationError.LoginFailed);
            }

            if (!response.IsSuccessStatusCode)
            {
                return AuthEndpointResult.NoFallback(AuthenticationError.LoginFailed);
            }

            var responseContent = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return AuthEndpointResult.FallbackAllowed(AuthenticationError.LoginFailed);
            }

            AuthenticationResponse? authenticationResponse;
            try
            {
                authenticationResponse = JsonSerializer.Deserialize<AuthenticationResponse>(responseContent, JsonOptions);
            }
            catch (JsonException)
            {
                return AuthEndpointResult.FallbackAllowed(AuthenticationError.LoginFailed);
            }

            var accessToken = authenticationResponse?.AccessToken;
            var userId = authenticationResponse?.User?.Id;
            var resolvedUserName = authenticationResponse?.User?.Name;

            if (string.IsNullOrWhiteSpace(accessToken)
                || string.IsNullOrWhiteSpace(userId)
                || string.IsNullOrWhiteSpace(resolvedUserName))
            {
                return AuthEndpointResult.FallbackAllowed(AuthenticationError.LoginFailed);
            }

            var serverId = authenticationResponse?.ServerId;
            if (string.IsNullOrWhiteSpace(serverId))
            {
                serverId = authenticationResponse?.User?.ServerId;
            }

            var session = new AuthSession(
                serverBase,
                accessToken,
                userId,
                resolvedUserName,
                serverId ?? string.Empty);

            return AuthEndpointResult.Success(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AuthEndpointResult.NoFallback(AuthenticationError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return AuthEndpointResult.NoFallback(AuthenticationError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return AuthEndpointResult.NoFallback(AuthenticationError.ServerUnreachable);
        }
    }

    private static Uri BuildEndpointUri(string serverBase, string endpointPath)
    {
        return new Uri($"{serverBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private AuthenticationResult AuthenticationFailure(AuthenticationError error)
    {
        if (error != AuthenticationError.Cancelled)
        {
            diagnostics.Write("authentication", "failed", $"reason={error}");
        }

        return AuthenticationResult.Failure(error);
    }

    private sealed record AuthenticateByNameRequest(
        [property: JsonPropertyName("Username")] string Username,
        [property: JsonPropertyName("Pw")] string Pw);

    private sealed class AuthenticationResponse
    {
        public string? AccessToken { get; set; }

        public string? ServerId { get; set; }

        public AuthenticationUser? User { get; set; }
    }

    private sealed class AuthenticationUser
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? ServerId { get; set; }
    }

    private sealed class AuthEndpointResult
    {
        private AuthEndpointResult(
            bool isSuccess,
            bool canFallback,
            AuthSession? session,
            AuthenticationError error)
        {
            IsSuccess = isSuccess;
            CanFallback = canFallback;
            Session = session;
            Error = error;
        }

        public bool IsSuccess { get; }

        public bool CanFallback { get; }

        public AuthSession? Session { get; }

        public AuthenticationError Error { get; }

        public AuthenticationResult ToAuthenticationResult()
        {
            return IsSuccess && Session is not null
                ? AuthenticationResult.Success(Session)
                : AuthenticationResult.Failure(Error);
        }

        public static AuthEndpointResult Success(AuthSession session)
        {
            return new AuthEndpointResult(true, false, session, AuthenticationError.None);
        }

        public static AuthEndpointResult FallbackAllowed(AuthenticationError error)
        {
            return new AuthEndpointResult(false, true, null, error);
        }

        public static AuthEndpointResult NoFallback(AuthenticationError error)
        {
            return new AuthEndpointResult(false, false, null, error);
        }
    }
}
