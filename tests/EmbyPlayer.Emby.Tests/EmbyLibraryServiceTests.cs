using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyLibraryServiceTests
{
    [DataTestMethod]
    [DataRow(LibrarySortField.Title, LibrarySortDirection.Ascending, "SortName", "Ascending")]
    [DataRow(LibrarySortField.Title, LibrarySortDirection.Descending, "SortName", "Descending")]
    [DataRow(LibrarySortField.DateAdded, LibrarySortDirection.Ascending, "DateCreated,SortName", "Ascending")]
    [DataRow(LibrarySortField.DateAdded, LibrarySortDirection.Descending, "DateCreated,SortName", "Descending")]
    [DataRow(LibrarySortField.Year, LibrarySortDirection.Ascending, "ProductionYear,SortName", "Ascending")]
    [DataRow(LibrarySortField.Year, LibrarySortDirection.Descending, "ProductionYear,SortName", "Descending")]
    public async Task Query_MapsEverySortAndDirectionToServer(
        LibrarySortField field, LibrarySortDirection direction, string expectedSort, string expectedOrder)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateItemsResponse("Movie")));
        var service = CreateService(handler);

        await service.LoadLibraryItemsAsync(CreateSession(), new LibraryItem("library-1", "Movies", "movies"),
            100, 100, new LibraryQuery(field, direction), CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests.Single().Uri.Query);
        StringAssert.Contains(query, $"SortBy={expectedSort}&");
        StringAssert.Contains(query, $"SortOrder={expectedOrder}&");
        StringAssert.Contains(query, "StartIndex=100&Limit=100");
        Assert.IsFalse(query.Contains("IsPlayed="));
        Assert.IsFalse(query.Contains("IsFavorite="));
    }

    [DataTestMethod]
    [DataRow(LibraryWatchedFilter.All, false, "")]
    [DataRow(LibraryWatchedFilter.All, true, "")]
    [DataRow(LibraryWatchedFilter.Unwatched, false, "IsPlayed=false")]
    [DataRow(LibraryWatchedFilter.Unwatched, true, "IsPlayed=false")]
    [DataRow(LibraryWatchedFilter.Watched, false, "IsPlayed=true")]
    [DataRow(LibraryWatchedFilter.Watched, true, "IsPlayed=true")]
    public async Task Query_CombinesWatchAndFavoriteFilters(
        LibraryWatchedFilter watched, bool favoritesOnly, string expectedPlayed)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateItemsResponse("Series")));
        var service = CreateService(handler);

        await service.LoadLibraryItemsAsync(CreateSession(), new LibraryItem("library-2", "TV", "tvshows"),
            0, 100, new LibraryQuery(WatchedFilter: watched, FavoritesOnly: favoritesOnly), CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests.Single().Uri.Query);
        if (expectedPlayed.Length > 0)
        {
            StringAssert.Contains(query, expectedPlayed);
        }
        else
        {
            Assert.IsFalse(query.Contains("IsPlayed="));
        }

        Assert.AreEqual(favoritesOnly, query.Contains("IsFavorite=true"));
        StringAssert.Contains(query, "IncludeItemTypes=Series");
        StringAssert.Contains(query, "EnableUserData=true");
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
    }

    [TestMethod]
    public async Task Query_RouteFallbackKeepsQueryAndEmptyResult()
    {
        var handler = new RecordingHttpMessageHandler((_, attempt, _) => Task.FromResult(
            CreateResponse(attempt == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK, "{\"Items\":[]}")));
        var service = CreateService(handler);

        var result = await service.LoadLibraryItemsAsync(CreateSession(), new LibraryItem("library-1", "Movies", "movies"),
            0, 100, new LibraryQuery(LibrarySortField.Year, FavoritesOnly: true), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(handler.Requests[0].Uri.Query, handler.Requests[1].Uri.Query);
        Assert.AreEqual("/emby/Users/user-1/Items", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task Query_CallerCancellationReachesHttpAndReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHttpMessageHandler(async (_, _, token) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return CreateItemsResponse("Movie");
        });
        var service = CreateService(handler);
        var load = service.LoadLibraryItemsAsync(CreateSession(), new LibraryItem("library-1", "Movies", "movies"),
            0, 100, new LibraryQuery(FavoritesOnly: true), cancellation.Token);
        await requestStarted.Task;

        cancellation.Cancel();
        var result = await load;

        Assert.AreEqual(LibraryLoadError.Cancelled, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadLibrariesAsync_RequestsUserViewsEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateLibrariesResponse()));
        var service = CreateService(handler);

        var result = await service.LoadLibrariesAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Views", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("Movies", result.Libraries[0].Name);
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateItemsResponse("Movie")));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbyLibraryService(new HttpClient(handler), deviceIdService);

        var result = await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Movies", "movies"),
            0,
            100,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [DataTestMethod]
    [DataRow("movies", "Movie")]
    [DataRow("movie", "Movie")]
    [DataRow("tvshows", "Series")]
    [DataRow("tv", "Series")]
    [DataRow("collections", "BoxSet")]
    [DataRow("boxsets", "BoxSet")]
    [DataRow("playlists", "Playlist")]
    public async Task LoadLibraryItemsAsync_UsesTypeSpecificIncludeItemTypes(
        string libraryType,
        string expectedItemType)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateItemsResponse(expectedItemType)));
        var service = CreateService(handler);

        await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Library", libraryType),
            0,
            100,
            CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests[0].Uri.Query);
        Assert.AreEqual("/Users/user-1/Items", handler.Requests[0].Uri.AbsolutePath);
        StringAssert.Contains(query, "ParentId=library-1");
        StringAssert.Contains(query, "StartIndex=0");
        StringAssert.Contains(query, "Limit=100");
        StringAssert.Contains(query, "SortBy=SortName");
        StringAssert.Contains(query, "SortOrder=Ascending");
        StringAssert.Contains(query, $"IncludeItemTypes={expectedItemType}");
        StringAssert.Contains(query, "Recursive=true");
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_TvShowsRequestSeriesAndNotEpisodes()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateItemsResponse("Series")));
        var service = CreateService(handler);

        await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "TV Shows", "tvshows"),
            0,
            100,
            CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests[0].Uri.Query);
        StringAssert.Contains(query, "IncludeItemTypes=Series");
        Assert.IsFalse(query.Contains("IncludeItemTypes=Episode", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_UsesRequestedStartIndexAndLimit()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateItemsResponse("Movie")));
        var service = CreateService(handler);

        await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Movies", "movies"),
            100,
            100,
            CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests[0].Uri.Query);
        StringAssert.Contains(query, "StartIndex=100");
        StringAssert.Contains(query, "Limit=100");
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_MapsTotalRecordCount()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"TotalRecordCount\":250,\"Items\":[{\"Id\":\"movie-1\",\"Name\":\"Movie One\",\"Type\":\"Movie\"}]}")));
        var service = CreateService(handler);

        var result = await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Movies", "movies"),
            0,
            100,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(250, result.TotalRecordCount);
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_UnknownTypeDoesNotFailOrForceIncludeItemTypes()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateItemsResponse("Movie")));
        var service = CreateService(handler);

        var result = await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Mixed", "mixed"),
            0,
            100,
            CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests[0].Uri.Query);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(query.Contains("IncludeItemTypes=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_MapsItemsAndKeepsItemsWithoutPoster()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"movie-1\",\"Name\":\"Movie One\",\"Type\":\"Movie\",\"ProductionYear\":2024,\"UserData\":{\"PlayedPercentage\":40,\"Played\":true}},{\"Id\":\"movie-2\",\"Name\":\"Movie Two\",\"Type\":\"Movie\"}]}")));
        var service = CreateService(handler);

        var result = await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Movies", "movies"),
            0,
            100,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual("Movie One", result.Items[0].Title);
        Assert.AreEqual(40, result.Items[0].PlayedPercentage);
        Assert.IsTrue(result.Items[0].IsPlayed);
        Assert.IsNull(result.Items[1].PosterUrl);
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_PosterUrlDoesNotContainAccessToken()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateItemsResponse("Movie")));
        var service = CreateService(handler);

        var result = await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Movies", "movies"),
            0,
            100,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var posterUrl = result.Items[0].PosterUrl;
        Assert.IsFalse(string.IsNullOrWhiteSpace(posterUrl));
        Assert.IsFalse(posterUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(posterUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [DataTestMethod]
    [DataRow(401, LibraryLoadError.Unauthorized)]
    [DataRow(403, LibraryLoadError.Forbidden)]
    public async Task LoadLibraryItemsAsync_AuthenticationFailureMapsWithoutFallback(
        int statusCode,
        LibraryLoadError expectedError)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"unauthorized\"}")));
        var service = CreateService(handler);

        var result = await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Movies", "movies"),
            0,
            100,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(expectedError, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task LoadLibrariesAsync_PathMismatchFallsBackToEmbyPath(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}")
                : CreateLibrariesResponse()));
        var service = CreateService(handler);

        var result = await service.LoadLibrariesAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Views", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/user-1/Views", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task LoadLibraryItemsAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("network unavailable"));
        var service = CreateService(handler);

        var result = await service.LoadLibraryItemsAsync(
            CreateSession(),
            new LibraryItem("library-1", "Movies", "movies"),
            0,
            100,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(LibraryLoadError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    private static EmbyLibraryService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbyLibraryService(
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

    private static HttpResponseMessage CreateLibrariesResponse()
    {
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"Items\":[{\"Id\":\"library-1\",\"Name\":\"Movies\",\"CollectionType\":\"movies\"}]}");
    }

    private static HttpResponseMessage CreateItemsResponse(string itemType)
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
}
