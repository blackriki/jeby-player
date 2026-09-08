using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Caching;
using EmbyPlayer.Core.Devices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyCacheManagementServiceTests
{
    [DataTestMethod]
    [DataRow(HttpStatusCode.Unauthorized, CacheOperationError.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden, CacheOperationError.Forbidden)]
    [DataRow(HttpStatusCode.InternalServerError, CacheOperationError.ServerUnreachable)]
    public async Task RebuildSearchIndexAsync_ReportsHttpFailureAndKeepsRetryAvailable(HttpStatusCode status, CacheOperationError expected)
    {
        using var context = new Context(new ResponseHandler(() => new HttpResponseMessage(status)));
        var error = await Assert.ThrowsExceptionAsync<CacheOperationException>(() =>
            context.Service.RebuildSearchIndexAsync(Session, CancellationToken.None));
        Assert.AreEqual(expected, error.Error);
        Assert.AreEqual(0L, (await context.Service.GetUsageAsync(CancellationToken.None)).SearchIndexDiskBytes);
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("not json")]
    [DataRow("{\"Items\":[null]}")]
    [DataRow("")]
    public async Task RebuildSearchIndexAsync_RejectsInvalidMediaResponse(string json)
    {
        using var context = new Context(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var error = await Assert.ThrowsExceptionAsync<CacheOperationException>(() =>
            context.Service.RebuildSearchIndexAsync(Session, CancellationToken.None));
        Assert.AreEqual(CacheOperationError.InvalidResponse, error.Error);
    }

    [TestMethod]
    public async Task GetUsageAndClearImages_UseTheLiveImageServiceAndPreserveSearchIndex()
    {
        using var context = new Context(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"Items\":[],\"TotalRecordCount\":0}") }));
        await context.Service.RebuildSearchIndexAsync(Session, CancellationToken.None);
        var diskBytes = (await context.Service.GetUsageAsync(CancellationToken.None)).SearchIndexDiskBytes;
        await context.Images.LoadImageAsync(Session, "http://media.local/Items/one/Images/Primary", CancellationToken.None);
        Assert.IsTrue((await context.Service.GetUsageAsync(CancellationToken.None)).ImageMemoryBytes > 0);

        await context.Service.ClearImagesAsync(CancellationToken.None);
        var usage = await context.Service.GetUsageAsync(CancellationToken.None);
        Assert.AreEqual(0L, usage.ImageMemoryBytes);
        Assert.AreEqual(diskBytes, usage.SearchIndexDiskBytes);
        Assert.IsTrue(diskBytes > 0);
    }

    private static readonly AuthSession Session = new("http://media.local", "test-token", "user", "User", "server");

    private sealed class Context : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "EmbyPlayer.Tests", Guid.NewGuid().ToString("N"));
        public Context(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler);
            var device = new Device();
            Images = new EmbyImageService(client, device, Path.Combine(directory, "images"));
            Service = new EmbyCacheManagementService(Images, new LocalMediaSearchIndex(client, device, Path.Combine(directory, "index")));
        }
        public EmbyImageService Images { get; }
        public EmbyCacheManagementService Service { get; }
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Device : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult("test-device");
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }
}
