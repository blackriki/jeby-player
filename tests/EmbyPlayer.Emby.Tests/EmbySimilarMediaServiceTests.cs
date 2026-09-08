using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Devices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbySimilarMediaServiceTests
{
    [DataTestMethod]
    [DataRow("Series", "Series", "Movie")]
    [DataRow("sErIeS", "Series", "Movie")]
    [DataRow("Movie", "Movie", "Series")]
    [DataRow("mOvIe", "Movie", "Series")]
    public async Task LoadSimilarAsync_UsesRequestedTypeAndMapsFilteredLimitedServerOrder(
        string itemType,
        string expectedType,
        string excludedType)
    {
        var items = new List<string>
        {
            $"{{\"Id\":\"CURRENT-1\",\"Name\":\"Current\",\"Type\":\"{expectedType}\"}}",
            $"{{\"Name\":\"Missing id\",\"Type\":\"{expectedType}\"}}",
            "{\"Id\":\"episode-1\",\"Name\":\"Episode\",\"Type\":\"Episode\"}"
        };
        items.AddRange(Enumerable.Range(0, 14).Select(index =>
            $"{{\"Id\":\"similar-{index}\",\"Name\":\"Similar {index}\",\"Type\":\"{(index == 0 ? excludedType : index == 12 ? expectedType.ToLowerInvariant() : expectedType)}\",\"ProductionYear\":{2000 + index},\"RunTimeTicks\":1000,\"ImageTags\":{{\"Primary\":\"poster tag/{index}\"}},\"UserData\":{{\"PlayedPercentage\":{(index == 2 ? 35 : 0)},\"PlaybackPositionTicks\":{(index == 1 ? 250 : 0)},\"Played\":{(index == 2 ? "true" : "false")}}}}}"));
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(
            CreateResponse(HttpStatusCode.OK, $"{{\"Items\":[{string.Join(",", items)}]}}")));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "current-1", itemType, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(12, result.Items.Count);
        Assert.AreEqual("similar-1", result.Items[0].Id);
        Assert.AreEqual("Similar 1", result.Items[0].Title);
        Assert.AreEqual(expectedType, result.Items[0].Type);
        Assert.AreEqual(2001, result.Items[0].Year);
        Assert.AreEqual(25, result.Items[0].PlayedPercentage);
        Assert.AreEqual(250L, result.Items[0].ResumePositionTicks);
        Assert.AreEqual(35, result.Items[1].PlayedPercentage);
        Assert.IsTrue(result.Items[1].IsPlayed);
        Assert.AreEqual("similar-12", result.Items[^1].Id);
        Assert.AreEqual(expectedType.ToLowerInvariant(), result.Items[^1].Type);
        Assert.AreEqual(
            "http://media.local:8096/Items/similar-1/Images/Primary?tag=poster%20tag%2F1&maxHeight=360&quality=90",
            result.Items[0].PosterUrl);
        Assert.IsFalse(result.Items[0].PosterUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(1, handler.Requests.Count);
        var request = handler.Requests[0];
        Assert.AreEqual("/Items/current-1/Similar", request.Uri.AbsolutePath);
        StringAssert.Contains(request.Uri.Query, "UserId=user-1");
        StringAssert.Contains(request.Uri.Query, "Limit=12");
        StringAssert.Contains(request.Uri.Query, "EnableImages=true");
        StringAssert.Contains(request.Uri.Query, "ImageTypeLimit=1");
        StringAssert.Contains(request.Uri.Query, "EnableImageTypes=Primary");
        StringAssert.Contains(request.Uri.Query, "EnableUserData=true");
        StringAssert.Contains(request.Uri.Query, $"IncludeItemTypes={expectedType}");
        Assert.AreEqual("test-access-token", request.EmbyToken);
        StringAssert.Contains(request.EmbyAuthorization, "DeviceId=\"test-device-id\"");
    }

    [DataTestMethod]
    [DataRow("{\"Items\":[]}", "Series")]
    [DataRow("{}", "Series")]
    [DataRow("{\"Items\":[]}", "Movie")]
    [DataRow("{}", "Movie")]
    public async Task LoadSimilarAsync_EmptyItemsIsSuccessful(string content, string itemType)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, content)));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "item-1", itemType, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Items.Count);
    }

    [DataTestMethod]
    [DataRow("Series", 404)]
    [DataRow("Series", 405)]
    [DataRow("Movie", 404)]
    [DataRow("Movie", 405)]
    public async Task LoadSimilarAsync_PathMismatchFallsBackToEmbyPath(string itemType, int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) => Task.FromResult(
            index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{}")
                : CreateResponse(HttpStatusCode.OK,
                    $"{{\"Items\":[{{\"Id\":\"other-2\",\"Name\":\"Other\",\"Type\":\"{itemType}\",\"ImageTags\":{{\"Primary\":\"poster-tag\"}}}}]}}")));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "item-1", itemType, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/emby/Items/item-1/Similar", handler.Requests[1].Uri.AbsolutePath);
        StringAssert.Contains(handler.Requests[1].Uri.Query, $"IncludeItemTypes={itemType}");
        Assert.AreEqual("test-access-token", handler.Requests[1].EmbyToken);
        StringAssert.Contains(result.Items.Single().PosterUrl!, "/emby/Items/other-2/Images/Primary?");
    }

    [DataTestMethod]
    [DataRow("Series", 401, SimilarMediaLoadError.Unauthorized)]
    [DataRow("Series", 403, SimilarMediaLoadError.Forbidden)]
    [DataRow("Series", 500, SimilarMediaLoadError.ServerError)]
    [DataRow("Movie", 401, SimilarMediaLoadError.Unauthorized)]
    [DataRow("Movie", 403, SimilarMediaLoadError.Forbidden)]
    [DataRow("Movie", 500, SimilarMediaLoadError.ServerError)]
    public async Task LoadSimilarAsync_MapsNonSuccessWithoutFallback(
        string itemType,
        int statusCode,
        SimilarMediaLoadError expectedError)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode, "{}")));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "item-1", itemType, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(expectedError, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadSimilarAsync_MovieWithMissingOptionalMetadataRemainsAvailable()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            "{\"Items\":[{\"Id\":\"movie-2\",\"Type\":\"Movie\"}]}")));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "movie-1", "Movie", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var item = result.Items.Single();
        Assert.AreEqual("movie-2", item.Id);
        Assert.AreEqual(string.Empty, item.Title);
        Assert.IsNull(item.Year);
        Assert.IsNull(item.PosterUrl);
        Assert.IsNull(item.PlayedPercentage);
        Assert.IsNull(item.ResumePositionTicks);
        Assert.IsFalse(item.IsPlayed);
    }

    [DataTestMethod]
    [DataRow("Episode")]
    [DataRow("Season")]
    [DataRow("MusicAlbum")]
    [DataRow("")]
    public async Task LoadSimilarAsync_UnsupportedTypeReturnsEmptyWithoutRequest(string itemType)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new InvalidOperationException("Unsupported media must not request recommendations."));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "item-1", itemType, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadSimilarAsync_NotFoundAfterFallbackMapsNotFound()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NotFound, "{}")));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "series-1", "Series", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SimilarMediaLoadError.NotFound, result.Error);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadSimilarAsync_InvalidJsonMapsInvalidResponse()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, "not-json")));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "series-1", "Series", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SimilarMediaLoadError.InvalidResponse, result.Error);
    }

    [TestMethod]
    public async Task LoadSimilarAsync_NetworkFailureMapsServerUnreachable()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("offline"));
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "series-1", "Series", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SimilarMediaLoadError.ServerUnreachable, result.Error);
    }

    [TestMethod]
    public async Task LoadSimilarAsync_ExternalCancellationMapsCancelled()
    {
        var handler = new RecordingHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}");
        });
        var service = CreateService(handler);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.CancelAfter(TimeSpan.FromMilliseconds(30));

        var result = await service.LoadSimilarAsync(
            CreateSession(),
            "series-1",
            "Series",
            cancellationSource.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SimilarMediaLoadError.Cancelled, result.Error);
    }

    [TestMethod]
    public async Task LoadSimilarAsync_InternalTimeoutMapsServerTimeout()
    {
        var handler = new RecordingHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}");
        });
        var service = CreateService(handler);

        var result = await service.LoadSimilarAsync(CreateSession(), "series-1", "Series", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SimilarMediaLoadError.ServerTimeout, result.Error);
    }

    private static EmbySimilarMediaService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbySimilarMediaService(
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Emby-Token", out var tokenValues);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorizationValues);
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                tokenValues?.SingleOrDefault(),
                authorizationValues?.SingleOrDefault()));
            return handleRequest(request, Requests.Count - 1, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        string? EmbyToken,
        string? EmbyAuthorization);

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
}
