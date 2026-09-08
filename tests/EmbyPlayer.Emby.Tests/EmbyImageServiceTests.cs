using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Images;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyImageServiceTests
{
    [TestMethod]
    public async Task ClearMemoryCache_ReportsActualBytesAndRefetchesImages()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateImageResponse()));
        var service = CreateService(handler, cache.Path);
        var session = CreateSession();
        const string imageUrl = "http://media.local:8096/Items/item-1/Images/Primary";
        var first = await service.LoadImageAsync(session, imageUrl, CancellationToken.None);
        var cleared = 0;
        service.CacheCleared += (_, _) => cleared++;
        Assert.AreEqual((long)first.ImageBytes!.Length, service.GetMemoryCacheBytes());

        service.ClearMemoryCache();

        Assert.AreEqual(0L, service.GetMemoryCacheBytes());
        Assert.AreEqual(1, cleared);
        await service.LoadImageAsync(session, imageUrl, CancellationToken.None);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual((long)first.ImageBytes.Length, service.GetMemoryCacheBytes());
    }

    [TestMethod]
    public async Task ClearMemoryCache_InFlightDownloadCannotRepopulateClearedCache()
    {
        using var cache = new TempCacheDirectory();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHttpMessageHandler((_, index, _) => index == 0
            ? response.Task : Task.FromResult(CreateImageResponse()));
        var service = CreateService(handler, cache.Path);
        const string imageUrl = "http://media.local:8096/Items/item-1/Images/Primary";
        var pending = service.LoadImageAsync(CreateSession(), imageUrl, CancellationToken.None);
        service.ClearMemoryCache();
        response.SetResult(CreateImageResponse());
        Assert.IsTrue((await pending).IsSuccess);
        Assert.AreEqual(0L, service.GetMemoryCacheBytes());
        await service.LoadImageAsync(CreateSession(), imageUrl, CancellationToken.None);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadImageAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateImageResponse()));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbyImageService(new HttpClient(handler), deviceIdService, cache.Path);

        var result = await service.LoadImageAsync(
            CreateSession(),
            "http://media.local:8096/Items/item-1/Images/Primary",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [TestMethod]
    public async Task LoadImageAsync_CachesSuccessfulImageInMemory()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateImageResponse()));
        var service = CreateService(handler, cache.Path);
        var session = CreateSession();
        var imageUrl = "http://media.local:8096/Items/item-1/Images/Primary";

        var first = await service.LoadImageAsync(session, imageUrl, CancellationToken.None);
        var second = await service.LoadImageAsync(session, imageUrl, CancellationToken.None);

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(second.IsSuccess);
        CollectionAssert.AreEqual(first.ImageBytes!, second.ImageBytes!);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadImageAsync_DoesNotShareCachedImageAcrossUsers()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                index == 0 ? new byte[] { 1, 1, 1 } : new byte[] { 2, 2, 2 })));
        var service = CreateService(handler, cache.Path);
        var imageUrl = "http://media.local:8096/Items/item-1/Images/Primary";

        var first = await service.LoadImageAsync(
            CreateSession("user-1", "token-user-1"),
            imageUrl,
            CancellationToken.None);
        var second = await service.LoadImageAsync(
            CreateSession("user-2", "token-user-2"),
            imageUrl,
            CancellationToken.None);

        CollectionAssert.AreEqual(new byte[] { 1, 1, 1 }, first.ImageBytes!);
        CollectionAssert.AreEqual(new byte[] { 2, 2, 2 }, second.ImageBytes!);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadImageAsync_DoesNotAppendTokenToImageUrl()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateImageResponse()));
        var service = CreateService(handler, cache.Path);

        var result = await service.LoadImageAsync(
            CreateSession(),
            "http://media.local:8096/Items/item-1/Images/Primary",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(string.Empty, handler.Requests[0].Uri.Query);
        Assert.IsFalse(handler.Requests[0].Uri.AbsoluteUri.Contains("api_key", StringComparison.Ordinal));
        Assert.IsFalse(handler.Requests[0].Uri.AbsoluteUri.Contains("AccessToken", StringComparison.Ordinal));
        Assert.IsFalse(handler.Requests[0].Uri.AbsoluteUri.Contains("test-access-token", StringComparison.Ordinal));
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
    }

    [TestMethod]
    public async Task LoadImageAsync_DoesNotWriteDiskCacheFiles()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateImageResponse()));
        var service = CreateService(handler, cache.Path);

        await service.LoadImageAsync(
            CreateSession(),
            "http://media.local:8096/Items/item-1/Images/Primary",
            CancellationToken.None);

        Assert.IsFalse(Directory.Exists(cache.Path));
    }

    [TestMethod]
    public async Task LoadImageAsync_UnauthorizedMapsToUnauthorized()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized, Array.Empty<byte>())));
        var service = CreateService(handler, cache.Path);

        var result = await service.LoadImageAsync(
            CreateSession(),
            "http://media.local:8096/Items/item-1/Images/Primary",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ImageLoadError.Unauthorized, result.Error);
    }

    [TestMethod]
    public async Task LoadImageAsync_ServerFailureReturnsServerError()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.InternalServerError, Array.Empty<byte>())));
        var service = CreateService(handler, cache.Path);

        var result = await service.LoadImageAsync(
            CreateSession(),
            "http://media.local:8096/Items/item-1/Images/Primary",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ImageLoadError.ServerError, result.Error);
    }

    [TestMethod]
    public async Task LoadImageAsync_RequestFailureReturnsServerUnreachable()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("network unavailable"));
        var service = CreateService(handler, cache.Path);

        var result = await service.LoadImageAsync(
            CreateSession(),
            "http://media.local:8096/Items/item-1/Images/Primary",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ImageLoadError.ServerUnreachable, result.Error);
    }

    [TestMethod]
    public async Task LoadImageAsync_TimeoutReturnsServerTimeout()
    {
        using var cache = new TempCacheDirectory();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new OperationCanceledException());
        var service = CreateService(handler, cache.Path);

        var result = await service.LoadImageAsync(
            CreateSession(),
            "http://media.local:8096/Items/item-1/Images/Primary",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ImageLoadError.ServerTimeout, result.Error);
    }

    private static EmbyImageService CreateService(RecordingHttpMessageHandler handler, string cachePath)
    {
        return new EmbyImageService(
            new HttpClient(handler),
            new TestDeviceIdService("test-device-id"),
            cachePath);
    }

    private static AuthSession CreateSession(
        string userId = "user-1",
        string accessToken = "test-access-token")
    {
        return new AuthSession(
            "http://media.local:8096",
            accessToken,
            userId,
            "Test User",
            "server-1");
    }

    private static HttpResponseMessage CreateImageResponse()
    {
        return CreateResponse(HttpStatusCode.OK, new byte[] { 1, 2, 3, 4 });
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, byte[] content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(content)
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

    private sealed class TempCacheDirectory : IDisposable
    {
        public TempCacheDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "EmbyPlayerImageTests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
