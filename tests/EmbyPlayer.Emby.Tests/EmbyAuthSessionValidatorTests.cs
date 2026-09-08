using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyAuthSessionValidatorTests
{
    [TestMethod]
    public async Task ValidateAsync_RequestsSystemPingEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("/System/Ping", handler.Requests[0].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task ValidateAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var validator = new EmbyAuthSessionValidator(new HttpClient(handler), deviceIdService);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [DataTestMethod]
    [DataRow(200)]
    [DataRow(204)]
    public async Task ValidateAsync_TwoHundredStatusIsValidWithoutJson(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode, string.Empty)));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.AreEqual(AuthSessionValidationStatus.Valid, result.Status);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ValidateAsync_UnauthorizedMapsToInvalidTokenWithoutFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized)));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.AreEqual(AuthSessionValidationStatus.InvalidToken, result.Status);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ValidateAsync_ForbiddenMapsToForbiddenWithoutFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Forbidden)));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.AreEqual(AuthSessionValidationStatus.Forbidden, result.Status);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task ValidateAsync_PathMismatchFallsBackToEmbySystemPing(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode)
                : CreateResponse(HttpStatusCode.OK)));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/System/Ping", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/System/Ping", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task ValidateAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("connection refused"));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.AreEqual(AuthSessionValidationStatus.NetworkUnavailable, result.Status);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ValidateAsync_TimeoutDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new TaskCanceledException("timeout"));
        var validator = CreateValidator(handler);

        var result = await validator.ValidateAsync(CreateSession(), CancellationToken.None);

        Assert.AreEqual(AuthSessionValidationStatus.ServerTimeout, result.Status);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    private static EmbyAuthSessionValidator CreateValidator(RecordingHttpMessageHandler handler)
    {
        return new EmbyAuthSessionValidator(
            new HttpClient(handler),
            new TestDeviceIdService("test-device-id"));
    }

    private static AuthSession CreateSession()
    {
        return new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "Test User",
            "server-1");
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content = "pong")
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
            request.Headers.TryGetValues("X-Emby-Token", out var tokenValues);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorizationValues);
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                request.Method,
                tokenValues?.Single() ?? string.Empty,
                authorizationValues?.Single() ?? string.Empty));

            return await handleRequest(request, Requests.Count - 1, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        HttpMethod Method,
        string EmbyToken,
        string EmbyAuthorization);

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
}
