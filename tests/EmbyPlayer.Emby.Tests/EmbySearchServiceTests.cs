using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbySearchServiceTests
{
    [TestMethod]
    public async Task SearchAsync_RequestsUserItemsSearchEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSearchResponse("Movie")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "one piece", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Items", handler.Requests[0].Uri.AbsolutePath);

        var query = Uri.UnescapeDataString(handler.Requests[0].Uri.Query);
        StringAssert.Contains(query, "SearchTerm=one piece");
        StringAssert.Contains(query, "Recursive=true");
        StringAssert.Contains(query, "IncludeItemTypes=Movie,Series,Episode,BoxSet,Video");
        StringAssert.Contains(query, "Limit=50");
        StringAssert.Contains(query, "Fields=UserData,PrimaryImageAspectRatio,ProductionYear,SeriesName,IndexNumber,ParentIndexNumber,RunTimeTicks");
        Assert.IsNotNull(result.Items);
        Assert.AreEqual("Item One", result.Items![0].Title);
    }

    [TestMethod]
    public async Task SearchAsync_EncodesChineseSearchTermWithoutExactOrPrefixParameters()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSearchResponse("Movie")));
        var service = CreateService(handler);

        await service.SearchAsync(CreateSession(), "特务", CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests.Single().Uri.Query);
        StringAssert.Contains(query, "SearchTerm=特务");
        Assert.IsFalse(query.Contains("ExactMatch", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(query.Contains("StartsWith", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(query.Contains("NameStartsWith", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SearchAsync_ColdMiddleChineseKeywordUsesLocalIndexWithoutPrewarming()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}")));
        var localIndex = new StubLocalMediaSearchIndex(
            new LocalMediaSearchQueryResult(
                new[]
                {
                    new LocalMediaSearchMatch(
                        new LocalMediaSearchItem(
                            "spy-1",
                            "金特务",
                            null,
                            null,
                            null,
                            "Movie",
                            2025,
                            null,
                            null,
                            null),
                        2)
                },
                1,
                TimeSpan.FromMilliseconds(20),
                null,
                "http://media.local:8096"));
        var service = CreateService(handler, localIndex);

        var result = await service.SearchAsync(CreateSession(), "特务", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("金特务", result.Items!.Single().Title);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(1, localIndex.SearchCallCount);
    }

    [TestMethod]
    public async Task SearchAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSearchResponse("Movie")));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbySearchService(
            new HttpClient(handler),
            deviceIdService,
            new StubLocalMediaSearchIndex(LocalMediaSearchQueryResult.Empty));

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [TestMethod]
    public async Task SearchAsync_EmptyKeywordDoesNotSendRequest()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSearchResponse("Movie")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "   ", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Items);
        Assert.AreEqual(0, result.Items!.Count);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SearchAsync_MapsEpisodeWithoutFilteringMissingPoster()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"episode-1\",\"Name\":\"Episode Name\",\"Type\":\"Episode\",\"SeriesName\":\"Series One\",\"ParentIndexNumber\":1,\"IndexNumber\":6,\"UserData\":{\"PlayedPercentage\":25}}]}")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "series", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Items);
        Assert.AreEqual(1, result.Items!.Count);
        Assert.AreEqual("Series One S1:E6", result.Items[0].Title);
        Assert.AreEqual("Episode", result.Items[0].Type);
        Assert.AreEqual(25, result.Items[0].PlayedPercentage);
        Assert.IsNull(result.Items[0].PosterUrl);
    }

    [TestMethod]
    public async Task SearchAsync_PosterUrlDoesNotContainAccessToken()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSearchResponse("Movie")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Items);
        var posterUrl = result.Items![0].PosterUrl;
        Assert.IsFalse(string.IsNullOrWhiteSpace(posterUrl));
        Assert.IsFalse(posterUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(posterUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadFavoritesAsync_RequestsFavoriteItems()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSearchResponse("Movie")));
        var service = CreateService(handler);

        var result = await service.LoadFavoritesAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Items", handler.Requests[0].Uri.AbsolutePath);

        var query = Uri.UnescapeDataString(handler.Requests[0].Uri.Query);
        StringAssert.Contains(query, "Filters=IsFavorite");
        StringAssert.Contains(query, "Recursive=true");
        StringAssert.Contains(query, "IncludeItemTypes=Movie,Series,Episode,BoxSet,Video");
    }

    [TestMethod]
    public async Task SearchAsync_MapsFavoriteState()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"item-1\",\"Name\":\"Item One\",\"Type\":\"Movie\",\"UserData\":{\"IsFavorite\":true}}]}")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Items);
        Assert.IsTrue(result.Items![0].IsFavorite);
    }

    [TestMethod]
    public async Task SearchAsync_MapsPlayedState()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"item-1\",\"Name\":\"Item One\",\"Type\":\"Movie\",\"UserData\":{\"Played\":true}}]}")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Items);
        Assert.IsTrue(result.Items![0].IsPlayed);
    }

    [DataTestMethod]
    [DataRow(401, SearchLoadError.Unauthorized)]
    [DataRow(403, SearchLoadError.Forbidden)]
    public async Task SearchAsync_AuthenticationFailureMapsWithoutFallback(
        int statusCode,
        SearchLoadError expectedError)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"unauthorized\"}")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(expectedError, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task SearchAsync_PathMismatchFallsBackToEmbyPath(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}")
                : CreateSearchResponse("Movie")));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Items", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/user-1/Items", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task SearchAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("network unavailable"));
        var service = CreateService(handler);

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SearchLoadError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SearchAsync_DeduplicatesByIdAndOrdersExactBeforePrefixAndContains()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"shared\",\"Name\":\"金特务\",\"Type\":\"Movie\",\"UserData\":{\"IsFavorite\":true}}]}")));
        var localIndex = new StubLocalMediaSearchIndex(
            new LocalMediaSearchQueryResult(
                new[]
                {
                    CreateLocalMatch("shared", "金特务", 2),
                    CreateLocalMatch("prefix", "特务局", 1),
                    CreateLocalMatch("exact", "特务", 0)
                },
                3,
                null,
                null,
                "http://media.local:8096"));
        var service = CreateService(handler, localIndex);

        var result = await service.SearchAsync(CreateSession(), "特务", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Items);
        CollectionAssert.AreEqual(
            new[] { "exact", "prefix", "shared" },
            result.Items.Select(item => item.Id).ToArray());
        Assert.IsTrue(result.Items.Single(item => item.Id == "shared").IsFavorite);
        Assert.AreEqual(3, result.Diagnostics!.MergedResultCount);
    }

    [TestMethod]
    public async Task SearchAsync_DeduplicatedServerEpisodeKeepsKnownLocalMatchSource()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"episode-1\",\"Name\":\"Episode One\",\"Type\":\"Episode\",\"SeriesName\":\"Agent Series\"}]}")));
        var localItem = new LocalMediaSearchItem(
            "episode-1",
            "Episode One",
            null,
            "Episode One",
            "Agent Series",
            "Episode",
            2025,
            null,
            1,
            1);
        var localIndex = new StubLocalMediaSearchIndex(
            new LocalMediaSearchQueryResult(
                new[] { new LocalMediaSearchMatch(localItem, 4, SearchMatchSource.SeriesName) },
                1,
                null,
                null,
                "http://media.local:8096"));
        var service = CreateService(handler, localIndex);

        var result = await service.SearchAsync(CreateSession(), "agent", CancellationToken.None);

        var episode = result.Items!.Single();
        Assert.AreEqual("episode-1", episode.Id);
        Assert.IsNotNull(episode.MatchInfo);
        Assert.IsTrue(episode.MatchInfo.IsSeriesNameOnlyMatch);
        Assert.AreEqual("Agent Series", episode.MatchInfo.SeriesName);
    }

    [TestMethod]
    public async Task SearchAsync_LocalIndexFailureDoesNotHideServerResults()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSearchResponse("Movie")));
        var localIndex = new StubLocalMediaSearchIndex(
            LocalMediaSearchQueryResult.Empty,
            new IOException("index unavailable"));
        var service = CreateService(handler, localIndex);

        var result = await service.SearchAsync(CreateSession(), "movie", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("item-1", result.Items!.Single().Id);
        Assert.AreEqual(1, result.Diagnostics!.ServerResultCount);
        Assert.AreEqual(0, result.Diagnostics.LocalIndexResultCount);
    }

    private static LocalMediaSearchMatch CreateLocalMatch(string id, string name, int rank)
    {
        return new LocalMediaSearchMatch(
            new LocalMediaSearchItem(
                id,
                name,
                null,
                null,
                null,
                "Movie",
                2025,
                null,
                null,
                null),
            rank);
    }

    private static EmbySearchService CreateService(
        RecordingHttpMessageHandler handler,
        ILocalMediaSearchIndex? localMediaSearchIndex = null)
    {
        return new EmbySearchService(
            new HttpClient(handler),
            new TestDeviceIdService("test-device-id"),
            localMediaSearchIndex
                ?? new StubLocalMediaSearchIndex(LocalMediaSearchQueryResult.Empty));
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

    private static HttpResponseMessage CreateSearchResponse(string itemType)
    {
        return CreateResponse(
            HttpStatusCode.OK,
            $"{{\"Items\":[{{\"Id\":\"item-1\",\"Name\":\"Item One\",\"Type\":\"{itemType}\",\"ProductionYear\":2024,\"ImageTags\":{{\"Primary\":\"image-tag\"}},\"UserData\":{{\"PlayedPercentage\":35}}}}]}}");
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

    private sealed class StubLocalMediaSearchIndex : ILocalMediaSearchIndex
    {
        private readonly Exception? exception;
        private readonly LocalMediaSearchQueryResult result;

        public StubLocalMediaSearchIndex(
            LocalMediaSearchQueryResult result,
            Exception? exception = null)
        {
            this.result = result;
            this.exception = exception;
        }

        public int SearchCallCount { get; private set; }

        public Task PrepareAsync(AuthSession session, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RefreshAsync(AuthSession session, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<LocalMediaSearchQueryResult> SearchAsync(
            AuthSession session,
            string keyword,
            CancellationToken cancellationToken)
        {
            SearchCallCount++;
            return exception is null
                ? Task.FromResult(result)
                : Task.FromException<LocalMediaSearchQueryResult>(exception);
        }
    }
}
