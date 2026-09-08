using System.Diagnostics;
using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Home;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyHomeLoadingPerformanceTests
{
    [TestMethod]
    public async Task HomeContentRequestsOverlapAfterLibraryDiscovery()
    {
        var childrenStarted = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/Views"))
            {
                if (Interlocked.Increment(ref childrenStarted) == 5) allStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return EmptyResponse(request);
        }));
        var load = new EmbyHomeService(client, new Device()).LoadHomeAsync(Session(), default);
        var overlap = await Task.WhenAny(allStarted.Task, Task.Delay(600)) == allStarted.Task;
        release.TrySetResult();
        var result = await load;
        Assert.IsTrue(overlap, "Independent Home rows were blocked behind an unfinished content response.");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(5, childrenStarted, "Concurrency must not add duplicate requests.");
    }

    [TestMethod]
    public async Task HomeLoadingLatencyFixture()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref requests);
            await Task.Delay(120, token);
            return EmptyResponse(request);
        }));
        var watch = Stopwatch.StartNew();
        var result = await new EmbyHomeService(client, new Device()).LoadHomeAsync(Session(), default);
        watch.Stop();
        Console.WriteLine($"Home simulated 120ms endpoint latency: {watch.ElapsedMilliseconds}ms, {requests} requests.");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(6, requests);
    }

    [TestMethod]
    public async Task HomeCancellationReachesAllPendingContentRequests()
    {
        using var cancel = new CancellationTokenSource();
        var started = 0;
        var cancelled = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views")) return EmptyResponse(request);
            if (Interlocked.Increment(ref started) == 5) allStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return EmptyResponse(request);
        }));
        var load = new EmbyHomeService(client, new Device()).LoadHomeAsync(Session(), cancel.Token);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        Assert.AreEqual(HomeLoadError.Cancelled, (await load).Error);
        Assert.AreEqual(5, cancelled);
    }

    [TestMethod]
    public async Task ConcurrentUnauthorizedIsNotHiddenByAnotherRowsServerFailure()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Query.Contains("IsResumable") ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : request.RequestUri.AbsolutePath.EndsWith("/Latest") ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : EmptyResponse(request))));
        var result = await new EmbyHomeService(client, new Device()).LoadHomeAsync(Session(), default);
        Assert.AreEqual(HomeLoadError.Unauthorized, result.Error);
    }

    private static AuthSession Session() => new("http://home.fixture", "test-token", "user", "User", "server");
    private static HttpResponseMessage EmptyResponse(HttpRequestMessage request) => new(HttpStatusCode.OK)
    { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/Latest") ? "[]" : "{\"Items\":[]}") };
    private sealed class Device : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult("test-device");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
