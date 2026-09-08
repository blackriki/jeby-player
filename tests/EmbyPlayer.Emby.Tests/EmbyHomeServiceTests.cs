using System.Collections.Concurrent;
using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Home;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyHomeServiceTests
{
    private static HomeMediaSection GetSection(HomeData data, string id)
    {
        return data.MediaSections.Single(section => section.Id == id);
    }

    private static bool HasIncludeItemTypes(Uri uri, string expected)
    {
        return Uri.UnescapeDataString(uri.Query)
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(parts =>
                parts.Length == 2 &&
                string.Equals(parts[0], "IncludeItemTypes", StringComparison.Ordinal) &&
                string.Equals(parts[1], expected, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoadHomeAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((request, index, _) =>
            Task.FromResult(CreateSuccessResponse(request, index)));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbyHomeService(new HttpClient(handler), deviceIdService);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(6, handler.Requests.Count);
        Assert.AreEqual(1, deviceIdService.GetCallCount);
        foreach (var request in handler.Requests)
        {
            Assert.AreEqual("test-access-token", request.EmbyToken);
            StringAssert.Contains(request.EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        }
    }

    [TestMethod]
    public async Task LoadHomeAsync_MapsLibrariesContinueWatchingAndRecentlyAdded()
    {
        var handler = new RecordingHttpMessageHandler((request, index, _) =>
            Task.FromResult(CreateSuccessResponse(request, index)));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Test User", result.Data!.UserName);
        Assert.AreEqual("Movies", result.Data.Libraries[0].Name);
        Assert.AreEqual("Continue Movie", result.Data.ContinueWatching[0].Title);
        Assert.AreEqual("2020 \u00b7 \u7535\u5f71", result.Data.ContinueWatching[0].Subtitle);
        Assert.AreEqual(35, result.Data.ContinueWatching[0].PlayedPercentage);
        Assert.AreEqual(120000000, result.Data.ContinueWatching[0].ResumePositionTicks);
        Assert.IsTrue(result.Data.ContinueWatching[0].IsPlayed);
        StringAssert.Contains(result.Data.ContinueWatching[0].HeroImageUrl!, "/Items/continue-1/Images/Backdrop/0");
        Assert.AreEqual(
            "http://media.local:8096/Items/series-1/Images/Logo?tag=logo%20tag%2F%2B",
            result.Data.ContinueWatching[0].LogoUrl);
        Assert.IsFalse(result.Data.ContinueWatching[0].LogoUrl!.Contains(
            "token",
            StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("Recent Movie", result.Data.RecentlyAdded[0].Title);
        Assert.AreEqual("Category Movie", GetSection(result.Data, "movies").Items[0].Title);
        Assert.AreEqual("Category Series", GetSection(result.Data, "series").Items[0].Title);
        Assert.AreEqual("Category Box Set", GetSection(result.Data, "boxsets").Items[0].Title);
    }

    [TestMethod]
    public async Task LoadHomeAsync_ContinueWatchingUsesWideResumableQuery()
    {
        var handler = new RecordingHttpMessageHandler((request, index, _) =>
            Task.FromResult(CreateSuccessResponse(request, index)));
        var service = CreateService(handler);

        await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests[1].Uri.Query);
        Assert.AreEqual("/Users/user-1/Items", handler.Requests[1].Uri.AbsolutePath);
        StringAssert.Contains(query, "Recursive=true");
        StringAssert.Contains(query, "Filters=IsResumable");
        StringAssert.Contains(query, "Limit=24");
        StringAssert.Contains(query, "IncludeItemTypes=Movie,Episode,Video");
        StringAssert.Contains(query, "SeriesId");
        StringAssert.Contains(query, "SeasonId");
        StringAssert.Contains(query, "ParentLogoItemId");
        StringAssert.Contains(query, "ParentLogoImageTag");
        Assert.IsFalse(query.Contains("ParentId=", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(query.Contains("LibraryId=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadHomeAsync_RecentlyAddedUsesTwentyFourItemLimit()
    {
        var handler = new RecordingHttpMessageHandler((request, index, _) =>
            Task.FromResult(CreateSuccessResponse(request, index)));
        var service = CreateService(handler);

        await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requests[2].Uri.Query);
        Assert.AreEqual("/Users/user-1/Items/Latest", handler.Requests[2].Uri.AbsolutePath);
        StringAssert.Contains(query, "Limit=24");
    }

    [TestMethod]
    public async Task LoadHomeAsync_CategoryQueriesUseRecentLimitAndDateCreatedOrder()
    {
        var handler = new RecordingHttpMessageHandler((request, index, _) =>
            Task.FromResult(CreateSuccessResponse(request, index)));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        foreach (var itemType in new[] { "Movie", "Series", "BoxSet" })
        {
            var request = handler.Requests.Single(candidate =>
                HasIncludeItemTypes(candidate.Uri, itemType));
            var query = Uri.UnescapeDataString(request.Uri.Query);
            StringAssert.Contains(query, "Limit=24");
            StringAssert.Contains(query, "SortBy=DateCreated");
            StringAssert.Contains(query, "SortOrder=Descending");
        }
    }

    [TestMethod]
    public async Task LoadHomeAsync_AnimationUsesNamedLibraryParentAndMovieSeriesTypes()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(CreateResponseForPath(
                request,
                "{\"Items\":[{\"Id\":\"animation-library\",\"Name\":\"  动画  \",\"CollectionType\":\"mixed\"}]}")));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var animationRequest = handler.Requests.Single(candidate =>
            HasIncludeItemTypes(candidate.Uri, "Movie,Series"));
        var query = Uri.UnescapeDataString(animationRequest.Uri.Query);
        StringAssert.Contains(query, "ParentId=animation-library");
    }

    [TestMethod]
    public async Task LoadHomeAsync_AnimationMergesAllMatchingLibrariesByDateCreated()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateResponse(HttpStatusCode.OK,
                    "{\"Items\":[{\"Id\":\"anime-1\",\"Name\":\"动画电影\",\"CollectionType\":\"movies\"},{\"Id\":\"anime-2\",\"Name\":\"Kids\",\"CollectionType\":\"anime\"}]}"));
            }

            if (HasIncludeItemTypes(request.RequestUri, "Movie,Series"))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var content = query.Contains("ParentId=anime-1", StringComparison.Ordinal)
                    ? "{\"Items\":[{\"Id\":\"shared\",\"Name\":\"Shared\",\"Type\":\"Movie\",\"DateCreated\":\"2026-01-01T00:00:00Z\"},{\"Id\":\"older\",\"Name\":\"Older\",\"Type\":\"Movie\",\"DateCreated\":\"2024-01-01T00:00:00Z\"}]}"
                    : "{\"Items\":[{\"Id\":\"newer\",\"Name\":\"Newer\",\"Type\":\"Series\",\"DateCreated\":\"2027-01-01T00:00:00Z\"},{\"Id\":\"SHARED\",\"Name\":\"Duplicate\",\"Type\":\"Movie\",\"DateCreated\":\"2025-01-01T00:00:00Z\"}]}";
                return Task.FromResult(CreateResponse(HttpStatusCode.OK, content));
            }

            return Task.FromResult(CreateResponseForPath(request, "{\"Items\":[]}"));
        });
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(
            new[] { "newer", "shared", "older" },
            GetSection(result.Data!, "animation").Items.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task LoadHomeAsync_CategoryRowsDeduplicateItemsById()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (HasIncludeItemTypes(request.RequestUri!, "Movie"))
            {
                return Task.FromResult(CreateResponse(
                    HttpStatusCode.OK,
                    "{\"Items\":[{\"Id\":\"movie-1\",\"Name\":\"One\",\"Type\":\"Movie\"},{\"Id\":\"MOVIE-1\",\"Name\":\"Duplicate\",\"Type\":\"Movie\"}]}"));
            }

            return Task.FromResult(CreateResponseForPath(request, "{\"Items\":[]}"));
        });
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, GetSection(result.Data!, "movies").Items.Count);
    }

    [TestMethod]
    public async Task LoadHomeAsync_CategoryFailureDoesNotFailOtherHomeSections()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (HasIncludeItemTypes(request.RequestUri!, "Movie"))
            {
                return Task.FromResult(CreateResponse(HttpStatusCode.InternalServerError, "{}"));
            }

            return Task.FromResult(CreateResponseForPath(request, "{\"Items\":[]}"));
        });
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(GetSection(result.Data!, "movies").IsSuccess);
        Assert.IsTrue(GetSection(result.Data!, "series").IsSuccess);
        Assert.IsTrue(GetSection(result.Data!, "boxsets").IsSuccess);
    }

    [TestMethod]
    public async Task LoadHomeAsync_CategoryUnauthorizedPropagates()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(HasIncludeItemTypes(request.RequestUri!, "Movie")
                ? CreateResponse(HttpStatusCode.Unauthorized, "{}")
                : CreateResponseForPath(request, "{\"Items\":[]}")));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HomeLoadError.Unauthorized, result.Error);
    }

    [TestMethod]
    public async Task LoadHomeAsync_CategoryCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (HasIncludeItemTypes(request.RequestUri!, "Movie"))
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
            }

            return Task.FromResult(CreateResponseForPath(request, "{\"Items\":[]}"));
        });
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), cancellation.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HomeLoadError.Cancelled, result.Error);
    }

    [TestMethod]
    public async Task LoadHomeAsync_ContinueWatchingKeepsEpisodeWithoutPoster()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}"),
                1 => CreateResponse(
                    HttpStatusCode.OK,
                    "{\"Items\":[{\"Id\":\"episode-1\",\"Name\":\"Episode Name\",\"Type\":\"Episode\",\"SeriesName\":\"Test Series\",\"SeriesId\":\"series-1\",\"SeasonId\":\"season-1\",\"ParentIndexNumber\":1,\"IndexNumber\":6,\"UserData\":{\"PlayedPercentage\":48}}]}"),
                _ => CreateResponse(HttpStatusCode.OK, "[]")
            }));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Data!.ContinueWatching.Count);
        Assert.AreEqual("Test Series", result.Data.ContinueWatching[0].Title);
        Assert.AreEqual("S1:E6 \u00b7 Episode Name", result.Data.ContinueWatching[0].Subtitle);
        Assert.AreEqual("Episode", result.Data.ContinueWatching[0].Type);
        Assert.AreEqual("series-1", result.Data.ContinueWatching[0].SeriesId);
        Assert.AreEqual("season-1", result.Data.ContinueWatching[0].SeasonId);
        Assert.IsNull(result.Data.ContinueWatching[0].PosterUrl);
    }

    [TestMethod]
    public async Task LoadHomeAsync_EmptyArraysAreSuccess()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}"),
                1 => CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}"),
                _ => CreateResponse(HttpStatusCode.OK, "[]")
            }));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Data!.Libraries.Count);
        Assert.AreEqual(0, result.Data.ContinueWatching.Count);
        Assert.AreEqual(0, result.Data.RecentlyAdded.Count);
    }

    [TestMethod]
    public async Task LoadHomeAsync_PosterUrlDoesNotContainAccessToken()
    {
        var handler = new RecordingHttpMessageHandler((request, index, _) =>
            Task.FromResult(CreateSuccessResponse(request, index)));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var posterUrl = result.Data!.ContinueWatching[0].PosterUrl;
        Assert.IsFalse(string.IsNullOrWhiteSpace(posterUrl));
        Assert.IsFalse(posterUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(posterUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadHomeAsync_HeroImagePrefersBackdropThenThumbThenArtThenPrimary()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}"),
                1 => CreateResponse(
                    HttpStatusCode.OK,
                    "{\"Items\":[{\"Id\":\"backdrop-1\",\"Name\":\"Backdrop\",\"Type\":\"Movie\",\"BackdropImageTags\":[\"wide\"]},{\"Id\":\"thumb-1\",\"Name\":\"Thumb\",\"Type\":\"Movie\",\"ImageTags\":{\"Primary\":\"primary\",\"Thumb\":\"thumb\"}},{\"Id\":\"art-1\",\"Name\":\"Art\",\"Type\":\"Movie\",\"ImageTags\":{\"Primary\":\"primary\",\"Art\":\"art\"}},{\"Id\":\"primary-1\",\"Name\":\"Primary\",\"Type\":\"Movie\",\"ImageTags\":{\"Primary\":\"primary\"}}]}"),
                _ => CreateResponse(HttpStatusCode.OK, "[]")
            }));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        StringAssert.Contains(result.Data!.ContinueWatching[0].HeroImageUrl!, "/Items/backdrop-1/Images/Backdrop/0");
        StringAssert.Contains(result.Data.ContinueWatching[1].HeroImageUrl!, "/Items/thumb-1/Images/Thumb");
        StringAssert.Contains(result.Data.ContinueWatching[2].HeroImageUrl!, "/Items/art-1/Images/Art");
        StringAssert.Contains(result.Data.ContinueWatching[3].HeroImageUrl!, "/Items/primary-1/Images/Primary");
        foreach (var card in result.Data.ContinueWatching)
        {
            Assert.IsFalse(card.HeroImageUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(card.HeroImageUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
        }
    }

    [DataTestMethod]
    [DataRow(401, HomeLoadError.Unauthorized)]
    [DataRow(403, HomeLoadError.Forbidden)]
    public async Task LoadHomeAsync_AuthenticationFailureMapsWithoutFallback(
        int statusCode,
        HomeLoadError expectedError)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"unauthorized\"}")));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(expectedError, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task LoadHomeAsync_LibraryPathMismatchFallsBackAndReusesEmbyApiBase(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((request, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}")
                : CreateSuccessResponse(request, index - 1)));
        var service = CreateService(handler);

        var result = await service.LoadHomeAsync(CreateSession(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(7, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Views", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/user-1/Views", handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/user-1/Items", handler.Requests[2].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/user-1/Items/Latest", handler.Requests[3].Uri.AbsolutePath);
    }

    private static EmbyHomeService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbyHomeService(
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

    private static HttpResponseMessage CreateSuccessResponse(HttpRequestMessage request, int index)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/Views", StringComparison.Ordinal))
        {
            return CreateResponse(HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"library-1\",\"Name\":\"Movies\",\"CollectionType\":\"movies\"}]}");
        }

        if (request.RequestUri.AbsolutePath.EndsWith("/Items/Latest", StringComparison.Ordinal))
        {
            return CreateResponse(HttpStatusCode.OK,
                "[{\"Id\":\"recent-1\",\"Name\":\"Recent Movie\",\"Type\":\"Movie\",\"ProductionYear\":2024}]");
        }

        if (Uri.UnescapeDataString(request.RequestUri.Query).Contains("Filters=IsResumable", StringComparison.Ordinal))
        {
            return CreateResponse(HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"continue-1\",\"Name\":\"Continue Movie\",\"Type\":\"Movie\",\"ProductionYear\":2020,\"ImageTags\":{\"Primary\":\"image-tag\"},\"BackdropImageTags\":[\"wide-tag\"],\"ParentLogoItemId\":\"series-1\",\"ParentLogoImageTag\":\"logo tag/+\",\"UserData\":{\"PlayedPercentage\":35,\"PlaybackPositionTicks\":120000000,\"Played\":true}}]}");
        }

        if (HasIncludeItemTypes(request.RequestUri, "Movie"))
        {
            return CreateResponse(HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"movie-1\",\"Name\":\"Category Movie\",\"Type\":\"Movie\"}]}");
        }

        if (HasIncludeItemTypes(request.RequestUri, "Series"))
        {
            return CreateResponse(HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"series-1\",\"Name\":\"Category Series\",\"Type\":\"Series\"}]}");
        }

        if (HasIncludeItemTypes(request.RequestUri, "BoxSet"))
        {
            return CreateResponse(HttpStatusCode.OK, "{\"Items\":[{\"Id\":\"boxset-1\",\"Name\":\"Category Box Set\",\"Type\":\"BoxSet\"}]}");
        }

        return index switch
        {
            0 => CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"library-1\",\"Name\":\"Movies\",\"CollectionType\":\"movies\"}]}"),
            1 => CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"continue-1\",\"Name\":\"Continue Movie\",\"Type\":\"Movie\",\"ProductionYear\":2020,\"ImageTags\":{\"Primary\":\"image-tag\"},\"BackdropImageTags\":[\"wide-tag\"],\"ParentLogoItemId\":\"series-1\",\"ParentLogoImageTag\":\"logo tag/+\",\"UserData\":{\"PlayedPercentage\":35,\"PlaybackPositionTicks\":120000000,\"Played\":true}}]}"),
            2 => CreateResponse(
                HttpStatusCode.OK,
                "[{\"Id\":\"recent-1\",\"Name\":\"Recent Movie\",\"Type\":\"Movie\",\"ProductionYear\":2024}]"),
            3 => CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"movie-1\",\"Name\":\"Category Movie\",\"Type\":\"Movie\"}]}"),
            4 => CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"series-1\",\"Name\":\"Category Series\",\"Type\":\"Series\"}]}"),
            _ => CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"boxset-1\",\"Name\":\"Category Box Set\",\"Type\":\"BoxSet\"}]}"),
        };
    }

    private static HttpResponseMessage CreateResponseForPath(
        HttpRequestMessage request,
        string librariesContent)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        if (path.EndsWith("/Views", StringComparison.Ordinal))
        {
            return CreateResponse(HttpStatusCode.OK, librariesContent);
        }

        if (path.EndsWith("/Items/Latest", StringComparison.Ordinal))
        {
            return CreateResponse(HttpStatusCode.OK, "[]");
        }

        if (query.Contains("Filters=IsResumable", StringComparison.Ordinal))
        {
            return CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}");
        }

        return CreateResponse(HttpStatusCode.OK, "{\"Items\":[]}");
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

        private readonly ConcurrentQueue<RequestSnapshot> requests = new();
        private int requestIndex;

        public IReadOnlyList<RequestSnapshot> Requests => requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Emby-Token", out var tokenValues);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorizationValues);
            var index = Interlocked.Increment(ref requestIndex) - 1;
            requests.Enqueue(new RequestSnapshot(
                request.RequestUri!,
                request.Method,
                tokenValues?.Single() ?? string.Empty,
                authorizationValues?.Single() ?? string.Empty));

            return handleRequest(request, index, cancellationToken);
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
