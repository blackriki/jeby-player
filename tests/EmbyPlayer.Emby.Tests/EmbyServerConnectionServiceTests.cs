using System.Net;
using EmbyPlayer.Core.Servers;
using EmbyPlayer.Core.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyServerConnectionServiceTests
{
    [TestMethod]
    public async Task ConnectAsync_Success_RequestsPublicInfoWithoutEmbyPathFirst()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.OK)));
        var service = CreateService(handler);

        var result = await service.ConnectAsync("http://media.local:8096", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("http://media.local:8096", result.Server!.ServerBase);
        Assert.AreEqual(1, handler.RequestUris.Count);
        Assert.AreEqual("/System/Info/Public", handler.RequestUris[0].AbsolutePath);
    }

    [TestMethod]
    public async Task ConnectAsync_PrimaryNotFound_FallsBackToEmbyPublicInfo()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateJsonResponse(HttpStatusCode.NotFound)
                : CreateJsonResponse(HttpStatusCode.OK)));
        var service = CreateService(handler);

        var result = await service.ConnectAsync("http://media.local:8096", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.RequestUris.Count);
        Assert.AreEqual("/System/Info/Public", handler.RequestUris[0].AbsolutePath);
        Assert.AreEqual("/emby/System/Info/Public", handler.RequestUris[1].AbsolutePath);
    }

    [TestMethod]
    public async Task ConnectAsync_PrimaryNonJsonResponse_FallsBackToEmbyPublicInfo()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateTextResponse(HttpStatusCode.OK, "not json")
                : CreateJsonResponse(HttpStatusCode.OK)));
        var service = CreateService(handler);

        var result = await service.ConnectAsync("http://media.local:8096", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.RequestUris.Count);
        Assert.AreEqual("/System/Info/Public", handler.RequestUris[0].AbsolutePath);
        Assert.AreEqual("/emby/System/Info/Public", handler.RequestUris[1].AbsolutePath);
    }

    [TestMethod]
    public async Task ConnectAsync_NetworkFailure_DoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("connection refused"));
        var service = CreateService(handler);

        var result = await service.ConnectAsync("http://media.local:8096", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ServerConnectionError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.RequestUris.Count);
        Assert.AreEqual("/System/Info/Public", handler.RequestUris[0].AbsolutePath);
    }

    [TestMethod]
    public async Task ConnectAsync_NetworkFailure_WritesConnectionDiagnostic()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("connection refused"));
        var diagnostics = new TestApplicationDiagnostics();
        var service = new EmbyServerConnectionService(new HttpClient(handler), diagnostics);

        await service.ConnectAsync("http://media.local:8096", CancellationToken.None);

        Assert.AreEqual(1, diagnostics.Events.Count);
        var diagnostic = diagnostics.Events[0];
        Assert.AreEqual("connection", diagnostic.Category);
        Assert.AreEqual("failed", diagnostic.EventName);
        Assert.AreEqual("reason=ServerUnreachable", diagnostic.Details);
    }

    [TestMethod]
    public async Task ConnectAsync_Timeout_DoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new TaskCanceledException("timeout"));
        var service = CreateService(handler);

        var result = await service.ConnectAsync("http://media.local:8096", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ServerConnectionError.ServerTimeout, result.Error);
        Assert.AreEqual(1, handler.RequestUris.Count);
        Assert.AreEqual("/System/Info/Public", handler.RequestUris[0].AbsolutePath);
    }

    [TestMethod]
    public async Task ConnectAsync_Success_ReturnsResultWithoutSettingsDependency()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.OK)));
        var service = CreateService(handler);

        var result = await service.ConnectAsync("media.local:8096", CancellationToken.None);
        var constructorUsesSettings = typeof(EmbyServerConnectionService)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => typeof(IAppSettingsService).IsAssignableFrom(parameter.ParameterType));

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("http://media.local:8096", result.Server!.ServerBase);
        Assert.IsFalse(constructorUsesSettings);
    }

    private static EmbyServerConnectionService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbyServerConnectionService(new HttpClient(handler));
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"ServerName\":\"Test\"}")
        };
    }

    private static HttpResponseMessage CreateTextResponse(HttpStatusCode statusCode, string content)
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

        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return handleRequest(request, RequestUris.Count - 1, cancellationToken);
        }
    }
}
