using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.UserData;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyItemUserDataServiceTests
{
    [TestMethod]
    public async Task SetFavoriteAsync_WhenFavoriteTrue_PostsFavoriteEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));
        var service = CreateService(handler);

        var result = await service.SetFavoriteAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual("/Users/user-1/FavoriteItems/item-1", handler.Requests[0].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task SetFavoriteAsync_WhenFavoriteFalse_DeletesFavoriteEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.SetFavoriteAsync(CreateSession(), "item-1", false, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.AreEqual("/Users/user-1/FavoriteItems/item-1", handler.Requests[0].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task SetPlayedAsync_WhenPlayedTrue_PostsPlayedEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));
        var service = CreateService(handler);

        var result = await service.SetPlayedAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual("/Users/user-1/PlayedItems/item-1", handler.Requests[0].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task SetPlayedAsync_WhenPlayedFalse_DeletesPlayedEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.SetPlayedAsync(CreateSession(), "item-1", false, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.AreEqual("/Users/user-1/PlayedItems/item-1", handler.Requests[0].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task SetFavoriteAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK)));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbyItemUserDataService(new HttpClient(handler), deviceIdService);

        var result = await service.SetFavoriteAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [TestMethod]
    public async Task SetFavoriteAsync_UnauthorizedDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized)));
        var service = CreateService(handler);

        var result = await service.SetFavoriteAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ItemUserDataError.Unauthorized, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SetFavoriteAsync_ForbiddenDoesNotFallbackOrMasqueradeAsUnauthorized()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Forbidden)));
        var service = CreateService(handler);

        var result = await service.SetFavoriteAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ItemUserDataError.Forbidden, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SetPlayedAsync_ForbiddenDoesNotFallbackOrMasqueradeAsUnauthorized()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Forbidden)));
        var service = CreateService(handler);

        var result = await service.SetPlayedAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ItemUserDataError.Forbidden, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task SetFavoriteAsync_PathMismatchFallsBackToEmbyPath(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode)
                : CreateResponse(HttpStatusCode.OK)));
        var service = CreateService(handler);

        var result = await service.SetFavoriteAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/FavoriteItems/item-1", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/user-1/FavoriteItems/item-1", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task SetFavoriteAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("network unavailable"));
        var service = CreateService(handler);

        var result = await service.SetFavoriteAsync(CreateSession(), "item-1", true, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ItemUserDataError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    private static EmbyItemUserDataService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbyItemUserDataService(
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

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{}")
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

        protected override Task<HttpResponseMessage> SendAsync(
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

            return handleRequest(request, Requests.Count - 1, cancellationToken);
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
