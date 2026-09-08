using System.Net;
using System.Text.Json;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyPlaybackReportServiceTests
{
    [TestMethod]
    public async Task ReportPlayingAsync_PostsSessionsPlayingWithHeadersAndBody()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual("/Sessions/Playing", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        StringAssert.Contains(handler.Requests[0].Body, "\"itemId\":\"item-1\"");
        StringAssert.Contains(handler.Requests[0].Body, "\"mediaSourceId\":\"media-source-1\"");
        StringAssert.Contains(handler.Requests[0].Body, "\"playSessionId\":\"play-session-1\"");
        StringAssert.Contains(handler.Requests[0].Body, "\"positionTicks\":120000000");
        StringAssert.Contains(handler.Requests[0].Body, "\"isPaused\":false");
        StringAssert.Contains(handler.Requests[0].Body, "\"canSeek\":true");
        StringAssert.Contains(handler.Requests[0].Body, "\"playMethod\":\"DirectPlay\"");
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.AreEqual(12, body.RootElement.EnumerateObject().Count());
        Assert.IsFalse(body.RootElement.GetProperty("isMuted").GetBoolean());
        Assert.AreEqual(100, body.RootElement.GetProperty("volumeLevel").GetInt32());
        Assert.AreEqual(3, body.RootElement.GetProperty("audioStreamIndex").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("subtitleStreamIndex").ValueKind);
    }

    [TestMethod]
    public async Task ReportProgressAsync_PostsSessionsPlayingProgress()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.ReportProgressAsync(
            CreateSession(),
            new PlaybackReportProgressRequest(
                "item-1",
                "media-source-1",
                "play-session-1",
                120000000,
                54000000000,
                IsPaused: true,
                CanSeek: true,
                "DirectPlay",
                IsMuted: true,
                VolumeLevel: -20,
                AudioStreamIndex: 4,
                SubtitleStreamIndex: 7),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("/Sessions/Playing/Progress", handler.Requests[0].Uri.AbsolutePath);
        StringAssert.Contains(handler.Requests[0].Body, "\"isPaused\":true");
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.IsTrue(body.RootElement.GetProperty("isMuted").GetBoolean());
        Assert.AreEqual(0, body.RootElement.GetProperty("volumeLevel").GetInt32());
        Assert.AreEqual(4, body.RootElement.GetProperty("audioStreamIndex").GetInt32());
        Assert.AreEqual(7, body.RootElement.GetProperty("subtitleStreamIndex").GetInt32());
    }

    [TestMethod]
    public async Task ReportStoppedAsync_PostsSessionsPlayingStopped()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.ReportStoppedAsync(
            CreateSession(),
            new PlaybackReportStoppedRequest(
                "item-1",
                "media-source-1",
                "play-session-1",
                120000000,
                54000000000,
                IsPaused: false,
                CanSeek: true,
                "DirectPlay",
                IsMuted: false,
                VolumeLevel: 37,
                AudioStreamIndex: 5,
                SubtitleStreamIndex: null),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("/Sessions/Playing/Stopped", handler.Requests[0].Uri.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.IsFalse(body.RootElement.GetProperty("isMuted").GetBoolean());
        Assert.AreEqual(37, body.RootElement.GetProperty("volumeLevel").GetInt32());
        Assert.AreEqual(5, body.RootElement.GetProperty("audioStreamIndex").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("subtitleStreamIndex").ValueKind);
    }

    [DataTestMethod]
    [DataRow(401, PlaybackReportError.Unauthorized)]
    [DataRow(403, PlaybackReportError.Forbidden)]
    public async Task ReportAsync_AuthenticationStatusMapsDistinctlyWithoutFallback(
        int statusCode,
        PlaybackReportError expectedError)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode)));
        var service = CreateService(handler);

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(expectedError, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(500)]
    [DataRow(503)]
    public async Task ReportAsync_ServerErrorReturnsFailureWithoutThrowing(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode)));
        var service = CreateService(handler);

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackReportError.ServerError, result.Error);
    }

    [TestMethod]
    public async Task ReportAsync_HttpTimeoutMapsToServerTimeout()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("request timed out")));
        var service = CreateService(handler);

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackReportError.ServerTimeout, result.Error);
    }

    [TestMethod]
    public async Task ReportAsync_CallerCancellationMapsToCancelledWithoutThrowing()
    {
        var handler = new RecordingHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(HttpStatusCode.NoContent);
        });
        var service = CreateService(handler);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            cancellationSource.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackReportError.Cancelled, result.Error);
    }

    [TestMethod]
    public async Task ReportAsync_HttpRequestExceptionMapsToServerUnreachable()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("server unreachable")));
        var service = CreateService(handler);

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackReportError.ServerUnreachable, result.Error);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReportAsync_DeviceIdStorageFailureReturnsFailureWithoutThrowing(bool unauthorized)
    {
        Exception exception = unauthorized
            ? new UnauthorizedAccessException("settings denied")
            : new IOException("settings unavailable");
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NoContent)));
        var service = new EmbyPlaybackReportService(
            new HttpClient(handler),
            new ThrowingDeviceIdService(exception));

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackReportError.ServerError, result.Error);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ReportAsync_PathMismatchFallsBackToEmbyPath()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse(HttpStatusCode.NotFound)
                : CreateResponse(HttpStatusCode.NoContent)));
        var service = CreateService(handler);

        var result = await service.ReportPlayingAsync(
            CreateSession(),
            CreateStartRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/emby/Sessions/Playing", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task ReportProgressAsync_ServerFailureWritesProgressDiagnostic()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.InternalServerError)));
        var diagnostics = new TestApplicationDiagnostics();
        var service = new EmbyPlaybackReportService(
            new HttpClient(handler),
            new TestDeviceIdService("stable-device-id"),
            diagnostics);

        await service.ReportProgressAsync(
            CreateSession(),
            new PlaybackReportProgressRequest(
                "item-1",
                "media-source-1",
                "play-session-1",
                240000000,
                54000000000,
                IsPaused: false,
                CanSeek: true,
                "DirectPlay"),
            CancellationToken.None);

        Assert.IsTrue(diagnostics.Events.Any(entry =>
            entry.Category == "playback-report"
            && entry.EventName == "failed"
            && entry.Details == "kind=progress reason=ServerError"));
    }

    private static EmbyPlaybackReportService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbyPlaybackReportService(
            new HttpClient(handler),
            new TestDeviceIdService("stable-device-id"));
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

    private static PlaybackReportStartRequest CreateStartRequest()
    {
        return new PlaybackReportStartRequest(
            "item-1",
            "media-source-1",
            "play-session-1",
            120000000,
            54000000000,
            IsPaused: false,
            CanSeek: true,
            "DirectPlay",
            IsMuted: false,
            VolumeLevel: 140,
            AudioStreamIndex: 3,
            SubtitleStreamIndex: null);
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Emby-Token", out var tokenValues);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorizationValues);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                request.Method,
                tokenValues?.Single() ?? string.Empty,
                authorizationValues?.Single() ?? string.Empty,
                body));

            return await handleRequest(request, Requests.Count - 1, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        HttpMethod Method,
        string EmbyToken,
        string EmbyAuthorization,
        string Body);

    private sealed class TestDeviceIdService : IDeviceIdService
    {
        private readonly string deviceId;

        public TestDeviceIdService(string deviceId)
        {
            this.deviceId = deviceId;
        }

        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
        {
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
