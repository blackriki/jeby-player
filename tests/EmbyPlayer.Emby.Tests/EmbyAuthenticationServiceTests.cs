using System.Net;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyAuthenticationServiceTests
{
    [TestMethod]
    public async Task AuthenticateAsync_SendsEmbyAuthorizationHeader()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSuccessResponse()));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var authorization = handler.Requests[0].EmbyAuthorization;
        StringAssert.Contains(authorization, $"Emby Client=\"{ApplicationIdentity.Name}\"");
        StringAssert.Contains(authorization, "Device=\"Windows\"");
        StringAssert.Contains(authorization, "DeviceId=\"test-device-id\"");
        StringAssert.Contains(authorization, $"Version=\"{ApplicationIdentity.Version}\"");
        Assert.IsFalse(authorization.Contains("UserId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(authorization.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AuthenticateAsync_UsesDeviceIdService()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSuccessResponse()));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbyAuthenticationService(new HttpClient(handler), deviceIdService);

        await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.AreEqual(1, deviceIdService.GetCallCount);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
    }

    [TestMethod]
    public async Task AuthenticateAsync_DeviceIdStorageFailure_ReturnsLoginFailureAndWritesSafeDiagnostic()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSuccessResponse()));
        var diagnostics = new TestApplicationDiagnostics();
        const string storageDetails = "settings path contains private data";
        var service = new EmbyAuthenticationService(
            new HttpClient(handler),
            new ThrowingDeviceIdService(new IOException(storageDetails)),
            diagnostics);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AuthenticationError.LoginFailed, result.Error);
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.IsTrue(diagnostics.Events.Any(entry =>
            entry.Category == "authentication"
            && entry.EventName == "failed"
            && entry.Details == "reason=LoginFailed stage=device-id exceptionType=IOException"));
        Assert.IsFalse(diagnostics.Events.Any(entry =>
            entry.Details?.Contains(storageDetails, StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task AuthenticateAsync_DeviceIdCancellation_ReturnsCancelledWithoutFailureDiagnostic()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSuccessResponse()));
        var diagnostics = new TestApplicationDiagnostics();
        var service = new EmbyAuthenticationService(
            new HttpClient(handler),
            new ThrowingDeviceIdService(new OperationCanceledException(cancellationSource.Token)),
            diagnostics);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            cancellationSource.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AuthenticationError.Cancelled, result.Error);
        Assert.AreEqual(0, handler.Requests.Count);
        Assert.AreEqual(0, diagnostics.Events.Count);
    }


    [TestMethod]
    public async Task AuthenticateAsync_SendsUsernameAndPasswordBody()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSuccessResponse()));
        var service = CreateService(handler);

        await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Requests[0].Content!);
        Assert.AreEqual("test-user", document.RootElement.GetProperty("Username").GetString());
        Assert.AreEqual("test-password", document.RootElement.GetProperty("Pw").GetString());
    }

    [TestMethod]
    public async Task AuthenticateAsync_RequestsAuthenticateByNameEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSuccessResponse()));
        var service = CreateService(handler);

        await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.AreEqual("/Users/AuthenticateByName", handler.Requests[0].Uri.AbsolutePath);
    }

    [DataTestMethod]
    [DataRow(401)]
    [DataRow(403)]
    public async Task AuthenticateAsync_InvalidCredentials_DoNotFallback(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"invalid\"}")));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "wrong-password",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AuthenticationError.InvalidCredentials, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AuthenticateAsync_InvalidCredentials_WritesSafeAuthenticationDiagnostic()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized, "{\"Message\":\"invalid\"}")));
        var diagnostics = new TestApplicationDiagnostics();
        var service = new EmbyAuthenticationService(
            new HttpClient(handler),
            new TestDeviceIdService("test-device-id"),
            diagnostics);
        var password = string.Concat("wrong", " password");

        await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            password,
            CancellationToken.None);

        Assert.IsTrue(diagnostics.Events.Any(entry =>
            entry.Category == "authentication"
            && entry.EventName == "failed"
            && entry.Details == "reason=InvalidCredentials"));
        Assert.IsFalse(diagnostics.Events.Any(entry =>
            entry.Details?.Contains(password, StringComparison.Ordinal) == true));
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task AuthenticateAsync_PathMismatch_FallsBackToEmbyEndpoint(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}")
                : CreateSuccessResponse()));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Users/AuthenticateByName", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/AuthenticateByName", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task AuthenticateAsync_NonJsonSuccess_FallsBackToEmbyEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse(HttpStatusCode.OK, "not json")
                : CreateSuccessResponse()));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/emby/Users/AuthenticateByName", handler.Requests[1].Uri.AbsolutePath);
    }

    [DataTestMethod]
    [DataRow("{\"ServerId\":\"server-1\",\"User\":{\"Id\":\"user-1\",\"Name\":\"Test User\"}}")]
    [DataRow("{\"AccessToken\":\"test-access-token\",\"ServerId\":\"server-1\",\"User\":{\"Name\":\"Test User\"}}")]
    [DataRow("{\"AccessToken\":\"test-access-token\",\"ServerId\":\"server-1\",\"User\":{\"Id\":\"user-1\"}}")]
    public async Task AuthenticateAsync_MissingRequiredSuccessFields_Fails(string responseBody)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, responseBody)));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AuthenticationError.LoginFailed, result.Error);
    }

    [TestMethod]
    public async Task AuthenticateAsync_MissingServerId_DoesNotBlockLogin()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"AccessToken\":\"test-access-token\",\"User\":{\"Id\":\"user-1\",\"Name\":\"Test User\"}}")));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(string.Empty, result.Session!.ServerId);
    }

    [TestMethod]
    public async Task AuthenticateAsync_UsesUserServerIdWhenRootServerIdIsMissing()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"AccessToken\":\"test-access-token\",\"User\":{\"Id\":\"user-1\",\"Name\":\"Test User\",\"ServerId\":\"user-server-1\"}}")));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("user-server-1", result.Session!.ServerId);
    }

    [TestMethod]
    public async Task AuthenticateAsync_Success_ParsesAuthSession()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSuccessResponse()));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("http://media.local:8096", result.Session!.ServerBase);
        Assert.AreEqual("test-access-token", result.Session.AccessToken);
        Assert.AreEqual("user-1", result.Session.UserId);
        Assert.AreEqual("Test User", result.Session.UserName);
        Assert.AreEqual("server-1", result.Session.ServerId);
    }

    [TestMethod]
    public async Task AuthenticateAsync_NetworkFailure_DoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("connection refused"));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AuthenticationError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AuthenticateAsync_Timeout_DoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new TaskCanceledException("timeout"));
        var service = CreateService(handler);

        var result = await service.AuthenticateAsync(
            "http://media.local:8096",
            "test-user",
            "test-password",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AuthenticationError.ServerTimeout, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    private static EmbyAuthenticationService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbyAuthenticationService(
            new HttpClient(handler),
            new TestDeviceIdService("test-device-id"));
    }

    private static HttpResponseMessage CreateSuccessResponse()
    {
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"AccessToken\":\"test-access-token\",\"ServerId\":\"server-1\",\"User\":{\"Id\":\"user-1\",\"Name\":\"Test User\"}}");
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content)
        };
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handleRequest;

        public RecordingHttpMessageHandler(
            Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handleRequest)
        {
            this.handleRequest = handleRequest;
        }

        public List<RequestSnapshot> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestContent = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorizationValues);
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                request.Method,
                authorizationValues?.Single() ?? string.Empty,
                requestContent));

            return await handleRequest(request, Requests.Count - 1, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        HttpMethod Method,
        string EmbyAuthorization,
        string? Content);

    private sealed class TestDeviceIdService : IDeviceIdService
    {
        private readonly string deviceId;

        public TestDeviceIdService(string deviceId)
        {
            this.deviceId = deviceId;
        }

        public int GetCallCount { get; private set; }

        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
        {
            GetCallCount++;
            return Task.FromResult(deviceId);
        }
    }

    private sealed class ThrowingDeviceIdService : IDeviceIdService
    {
        private readonly Exception exception;

        public ThrowingDeviceIdService(Exception exception)
        {
            this.exception = exception;
        }

        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<string>(exception);
        }
    }
}
