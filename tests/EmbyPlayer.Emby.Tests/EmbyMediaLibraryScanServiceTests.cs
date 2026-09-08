using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyMediaLibraryScanServiceTests
{
    [TestMethod]
    public async Task RequestScanAsync_PostsAuthenticatedLibraryRefreshRequest()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.RequestScanAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual("/Library/Refresh", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("test-access-token", handler.Requests[0].Token);
        StringAssert.Contains(handler.Requests[0].Authorization, "DeviceId=\"test-device-id\"");
    }

    [DataTestMethod]
    [DataRow(401, MediaLibraryScanResult.FailureReason.Unauthorized)]
    [DataRow(403, MediaLibraryScanResult.FailureReason.Forbidden)]
    [DataRow(500, MediaLibraryScanResult.FailureReason.ServerError)]
    public async Task RequestScanAsync_MapsFailureStatus(int statusCode, MediaLibraryScanResult.FailureReason expected)
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)));
        var service = CreateService(handler);

        var result = await service.RequestScanAsync(CreateSession(), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(expected, result.Reason);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task RequestScanAsync_PathMismatchFallsBackToEmbyPath(int statusCode)
    {
        var handler = new RecordingHandler((index, _) =>
            Task.FromResult(new HttpResponseMessage(index == 0
                ? (HttpStatusCode)statusCode
                : HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.RequestScanAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Library/Refresh", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Library/Refresh", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task RequestScanAsync_PropagatesCallerCancellationAsCancelledResult()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var service = CreateService(handler);
        using var cancellation = new CancellationTokenSource();

        var request = service.RequestScanAsync(CreateSession(), cancellation.Token);
        cancellation.Cancel();
        var result = await request;

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(MediaLibraryScanResult.FailureReason.Cancelled, result.Reason);
    }

    private static EmbyMediaLibraryScanService CreateService(RecordingHandler handler)
    {
        return new EmbyMediaLibraryScanService(
            new HttpClient(handler),
            new TestDeviceIdService());
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> handler;

        public RecordingHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
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
            return handler(Requests.Count - 1, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        HttpMethod Method,
        string Token,
        string Authorization);

    private sealed class TestDeviceIdService : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult("test-device-id");
        }
    }
}
