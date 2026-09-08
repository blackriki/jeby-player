using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Home;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class HomeSectionServiceTests
{
    [DataTestMethod]
    [DataRow(HomeSectionKind.ContinueWatching, "Movie,Episode,Video", "DatePlayed")]
    [DataRow(HomeSectionKind.Movies, "Movie", "DateCreated")]
    [DataRow(HomeSectionKind.Series, "Series", "DateCreated")]
    [DataRow(HomeSectionKind.BoxSets, "BoxSet", "DateCreated")]
    public async Task LoadSectionAsync_RequestsGlobalPagedScopeWithAuthentication(
        HomeSectionKind section,
        string itemTypes,
        string sortBy)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response("{\"Items\":[]}")));
        var result = await CreateService(handler).LoadSectionAsync(Session(), section, 30, 30, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(30, result.NextStartIndex);
        Assert.IsFalse(result.HasMore);
        var request = handler.Requests.Single();
        Assert.AreEqual("/Users/user-1/Items", request.Uri.AbsolutePath);
        var query = Query(request.Uri);
        Assert.AreEqual("30", query["StartIndex"]);
        Assert.AreEqual("30", query["Limit"]);
        Assert.AreEqual("true", query["Recursive"]);
        Assert.AreEqual(itemTypes, query["IncludeItemTypes"]);
        Assert.AreEqual(sortBy, query["SortBy"]);
        Assert.AreEqual("Descending", query["SortOrder"]);
        Assert.AreEqual("true", query["EnableUserData"]);
        Assert.IsFalse(query.ContainsKey("ParentId"));
        if (section == HomeSectionKind.ContinueWatching)
        {
            Assert.AreEqual("IsResumable", query["Filters"]);
        }
        else
        {
            Assert.IsFalse(query.ContainsKey("Filters"));
            StringAssert.Contains(query["Fields"], "DateCreated");
        }

        Assert.AreEqual("test-access-token", request.Token);
        StringAssert.Contains(request.Authorization, "DeviceId=\"test-device-id\"");
    }

    [DataTestMethod]
    [DataRow(HomeSectionKind.Movies)]
    [DataRow(HomeSectionKind.RecentlyAdded)]
    public async Task LoadSectionAsync_PagesBeyondTheHomePreviewUntilExhausted(HomeSectionKind section)
    {
        var source = Enumerable.Range(0, 61).Select(Item).ToArray();
        var handler = new RecordingHandler((request, _) =>
        {
            var query = Query(request.RequestUri!);
            var page = source.Skip(int.Parse(query["StartIndex"])).Take(int.Parse(query["Limit"]));
            return Task.FromResult(Response(section == HomeSectionKind.RecentlyAdded
                ? JsonSerializer.Serialize(page)
                : JsonSerializer.Serialize(new { Items = page, TotalRecordCount = source.Length })));
        });
        var service = CreateService(handler);

        var first = await service.LoadSectionAsync(Session(), section, 0, 30, CancellationToken.None);
        var second = await service.LoadSectionAsync(Session(), section, first.NextStartIndex, 30, CancellationToken.None);
        var last = await service.LoadSectionAsync(Session(), section, second.NextStartIndex, 30, CancellationToken.None);

        Assert.AreEqual(30, first.Items.Count);
        Assert.AreEqual(30, second.Items.Count);
        Assert.AreEqual(1, last.Items.Count);
        Assert.IsTrue(first.HasMore);
        Assert.IsTrue(second.HasMore);
        Assert.IsFalse(last.HasMore);
        Assert.AreEqual(61, last.NextStartIndex);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 61).Select(index => $"item-{index}").ToArray(),
            first.Items.Concat(second.Items).Concat(last.Items).Select(item => item.Id).ToArray());
        if (section == HomeSectionKind.RecentlyAdded)
        {
            foreach (var request in handler.Requests)
            {
                Assert.AreEqual("/Users/user-1/Items/Latest", request.Uri.AbsolutePath);
                Assert.AreEqual("true", Query(request.Uri)["GroupItems"]);
            }
        }
    }

    [TestMethod]
    public async Task LoadSectionAsync_NextOffsetCountsRawRowsBeforeMissingMetadataOrUiDeduplication()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(
            """
            { "Items": [
                { "Id": "one", "Name": "One", "Type": "Movie" },
                { "Id": "ONE", "Name": "Duplicate", "Type": "Movie" },
                { "Id": "missing-name", "Type": "Movie" },
                { "Name": "Missing id", "Type": "Movie" }
            ], "TotalRecordCount": 5 }
            """)));

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Movies, 0, 4, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(4, result.NextStartIndex);
        Assert.IsTrue(result.HasMore);
    }

    [TestMethod]
    public async Task LoadSectionAsync_PreservesEpisodeNavigationResumeAndMissingImageMetadata()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(
            """
            { "Items": [ {
                "Id": "episode-1", "Name": "Episode title", "Type": "Episode",
                "SeriesName": "The Series", "SeriesId": "series-1", "ParentId": "season-1",
                "IndexNumber": 2, "ParentIndexNumber": 1, "RunTimeTicks": 1000,
                "UserData": { "PlaybackPositionTicks": 250, "IsFavorite": true, "Played": true }
            } ], "TotalRecordCount": 1 }
            """)));

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.ContinueWatching, 0, 30, CancellationToken.None);

        var item = result.Items.Single();
        Assert.AreEqual("The Series", item.Title);
        StringAssert.Contains(item.Subtitle, "S1:E2");
        Assert.AreEqual("series-1", item.SeriesId);
        Assert.AreEqual("season-1", item.SeasonId);
        Assert.AreEqual(250L, item.ResumePositionTicks);
        Assert.AreEqual(25d, item.PlayedPercentage);
        Assert.IsTrue(item.IsFavorite);
        Assert.IsTrue(item.IsPlayed);
        Assert.IsNull(item.PosterUrl);
        Assert.IsNull(item.HeroImageUrl);
        Assert.IsFalse(result.HasMore);
    }

    [TestMethod]
    public async Task LoadSectionAsync_AnimationMergesAllLibrariesBeforeGlobalPagination()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
            {
                return Task.FromResult(Response(AnimationLibraries));
            }

            var query = Query(request.RequestUri);
            var rangeStart = query["ParentId"] == "animation-a" ? 0 : 60;
            var source = Enumerable.Range(rangeStart, 80).Select(Item).ToArray();
            var page = source.Skip(int.Parse(query["StartIndex"])).Take(int.Parse(query["Limit"]));
            return Task.FromResult(Response(JsonSerializer.Serialize(new { Items = page, TotalRecordCount = source.Length })));
        });
        var service = CreateService(handler);

        var first = await service.LoadSectionAsync(Session(), HomeSectionKind.Animation, 0, 30, CancellationToken.None);
        var second = await service.LoadSectionAsync(Session(), HomeSectionKind.Animation, first.NextStartIndex, 30, CancellationToken.None);
        var last = await service.LoadSectionAsync(Session(), HomeSectionKind.Animation, 130, 30, CancellationToken.None);

        CollectionAssert.AreEqual(Enumerable.Range(0, 30).Select(index => $"item-{index}").ToArray(), first.Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(30, 30).Select(index => $"item-{index}").ToArray(), second.Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(130, 10).Select(index => $"item-{index}").ToArray(), last.Items.Select(item => item.Id).ToArray());
        Assert.IsTrue(first.HasMore);
        Assert.IsTrue(second.HasMore);
        Assert.IsFalse(last.HasMore);
        Assert.AreEqual(140, last.NextStartIndex);
        foreach (var request in handler.Requests.Where(request => !request.Uri.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)))
        {
            var query = Query(request.Uri);
            Assert.AreEqual("Movie,Series", query["IncludeItemTypes"]);
            Assert.IsTrue(query["ParentId"] is "animation-a" or "animation-b");
            Assert.IsTrue(int.Parse(query["Limit"]) <= 100);
            StringAssert.Contains(query["Fields"], "DateCreated");
        }
    }

    [TestMethod]
    public async Task LoadSectionAsync_AnimationReadsFurtherSourcePagesToReachGlobalOffset()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
            {
                return Task.FromResult(Response("{\"Items\":[{\"Id\":\"animation-a\",\"Name\":\"Anime\",\"CollectionType\":\"anime\"}]}"));
            }

            var query = Query(request.RequestUri);
            var page = Enumerable.Range(0, 180).Skip(int.Parse(query["StartIndex"]))
                .Take(int.Parse(query["Limit"])).Select(Item);
            return Task.FromResult(Response(JsonSerializer.Serialize(new { Items = page, TotalRecordCount = 180 })));
        });

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Animation, 120, 30, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(Enumerable.Range(120, 30).Select(index => $"item-{index}").ToArray(), result.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(150, result.NextStartIndex);
        Assert.IsTrue(result.HasMore);
        Assert.AreEqual(3, handler.Requests.Count);
        Assert.AreEqual("100", Query(handler.Requests[1].Uri)["Limit"]);
        Assert.AreEqual("100", Query(handler.Requests[2].Uri)["StartIndex"]);
        Assert.AreEqual("51", Query(handler.Requests[2].Uri)["Limit"]);
    }

    [TestMethod]
    public async Task LoadSectionAsync_AnimationFetchesBoundedPagesAndStopsIfServerRepeatsAPage()
    {
        var repeatedPage = JsonSerializer.Serialize(new
        {
            Items = Enumerable.Range(0, 100).Select(Item),
            TotalRecordCount = 1000
        });
        var handler = new RecordingHandler((request, _) => Task.FromResult(Response(
            request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)
                ? "{\"Items\":[{\"Id\":\"animation-a\",\"Name\":\"Anime\",\"CollectionType\":\"anime\"}]}"
                : repeatedPage)));

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Animation, 180, 30, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Items.Count);
        Assert.IsFalse(result.HasMore);
        Assert.AreEqual(3, handler.Requests.Count);
        Assert.AreEqual("100", Query(handler.Requests[1].Uri)["Limit"]);
        Assert.AreEqual("100", Query(handler.Requests[2].Uri)["StartIndex"]);
    }

    [TestMethod]
    public async Task LoadSectionAsync_AnimationWithoutMatchingLibrariesIsEmpty()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response("{\"Items\":[{\"Id\":\"movies\",\"Name\":\"Movies\",\"CollectionType\":\"movies\"}]}")));

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Animation, 0, 30, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Items.Count);
        Assert.IsFalse(result.HasMore);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task LoadSectionAsync_RouteMismatchFallsBackAndUsesResolvedImageBase(int status)
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.StartsWith("/emby/", StringComparison.Ordinal)
                ? Response("{\"Items\":[{\"Id\":\"movie-1\",\"Name\":\"Movie\",\"Type\":\"Movie\",\"ImageTags\":{\"Primary\":\"poster\"}}]}")
                : Response("{}", (HttpStatusCode)status)));

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Movies, 30, 30, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/emby/Users/user-1/Items", handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual("30", Query(handler.Requests[1].Uri)["StartIndex"]);
        Assert.AreEqual("test-access-token", handler.Requests[1].Token);
        StringAssert.Contains(result.Items.Single().PosterUrl!, "/emby/Items/movie-1/Images/Primary?");
        Assert.IsFalse(result.Items.Single().PosterUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [DataTestMethod]
    [DataRow(HomeSectionKind.Movies, 401, HomeLoadError.Unauthorized)]
    [DataRow(HomeSectionKind.Movies, 403, HomeLoadError.Forbidden)]
    [DataRow(HomeSectionKind.Movies, 500, HomeLoadError.ServerError)]
    [DataRow(HomeSectionKind.RecentlyAdded, 403, HomeLoadError.Forbidden)]
    [DataRow(HomeSectionKind.Animation, 401, HomeLoadError.Unauthorized)]
    [DataRow(HomeSectionKind.Animation, 403, HomeLoadError.Forbidden)]
    public async Task LoadSectionAsync_HttpErrorsRemainDistinctWithoutFallback(HomeSectionKind section, int status, HomeLoadError error)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response("{}", (HttpStatusCode)status)));

        var result = await CreateService(handler).LoadSectionAsync(Session(), section, 0, 30, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(error, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(401, HomeLoadError.Unauthorized)]
    [DataRow(403, HomeLoadError.Forbidden)]
    [DataRow(500, HomeLoadError.ServerError)]
    public async Task LoadSectionAsync_AnimationLibraryFailureDoesNotReturnAnIncompleteSuccess(int status, HomeLoadError error)
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal)
                ? Response(AnimationLibraries)
                : Query(request.RequestUri)["ParentId"] == "animation-a"
                    ? Response("{}", (HttpStatusCode)status)
                    : Response("{\"Items\":[]}")));

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Animation, 0, 30, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(error, result.Error);
    }

    [DataTestMethod]
    [DataRow(HomeSectionKind.Movies)]
    [DataRow(HomeSectionKind.RecentlyAdded)]
    public async Task LoadSectionAsync_InvalidResponseIsRecoverable(HomeSectionKind section)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response("invalid-json")));

        var result = await CreateService(handler).LoadSectionAsync(Session(), section, 0, 30, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HomeLoadError.InvalidResponse, result.Error);
    }

    [TestMethod]
    public async Task LoadSectionAsync_CancellationDuringRequestReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response("{}");
        });

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Movies, 0, 30, cancellation.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HomeLoadError.Cancelled, result.Error);
    }

    [TestMethod]
    public async Task LoadSectionAsync_NetworkFailureReturnsServerUnreachable()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("offline"));

        var result = await CreateService(handler).LoadSectionAsync(Session(), HomeSectionKind.Movies, 0, 30, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HomeLoadError.ServerUnreachable, result.Error);
    }

    private const string AnimationLibraries = """
        { "Items": [
            { "Id": "animation-a", "Name": "Animation", "CollectionType": "animation" },
            { "Id": "animation-b", "Name": "Anime", "CollectionType": "anime" },
            { "Id": "other", "Name": "Movies", "CollectionType": "movies" }
        ] }
        """;

    private static object Item(int index) => new
    {
        Id = $"item-{index}",
        Name = $"Item {index}",
        Type = index % 2 == 0 ? "Movie" : "Series",
        DateCreated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-index)
    };

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

    private static AuthSession Session() => new("http://media.local:8096", "test-access-token", "user-1", "Test User", "server-1");

    private static EmbyHomeService CreateService(RecordingHandler handler) => new(new HttpClient(handler), new TestDeviceIdService());

    private static HttpResponseMessage Response(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content)
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RequestSnapshot> requests = new();

        public IReadOnlyList<RequestSnapshot> Requests => requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Emby-Token", out var token);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorization);
            requests.Enqueue(new RequestSnapshot(request.RequestUri!, token?.Single(), authorization?.Single()));
            return handler(request, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(Uri Uri, string? Token, string? Authorization);

    private sealed class TestDeviceIdService : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult("test-device-id");
    }
}
