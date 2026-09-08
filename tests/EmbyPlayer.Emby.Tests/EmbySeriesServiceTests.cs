using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Series;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbySeriesServiceTests
{
    [TestMethod]
    public async Task LoadSeasonsAsync_RequestsShowsSeasonsEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateSeasonsResponse()));
        var service = CreateService(handler);

        var result = await service.LoadSeasonsAsync(
            CreateSession(),
            "series-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/Shows/series-1/Seasons", handler.Requests[0].Uri.AbsolutePath);
        StringAssert.Contains(handler.Requests[0].Uri.Query, "UserId=user-1");
        Assert.AreEqual("第 1 季", result.Seasons[0].Name);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_RequestsShowsEpisodesEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateEpisodesResponse()));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/Shows/series-1/Episodes", handler.Requests[0].Uri.AbsolutePath);
        var query = Uri.UnescapeDataString(handler.Requests[0].Uri.Query);
        StringAssert.Contains(query, "UserId=user-1");
        StringAssert.Contains(query, "SeasonId=season-1");
        StringAssert.Contains(query, "EnableUserData=true");
        StringAssert.Contains(query, "Fields=UserData,PrimaryImageAspectRatio,Overview,RunTimeTicks,SeriesName,IndexNumber,ParentIndexNumber,ImageTags");
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateEpisodesResponse()));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbySeriesService(new HttpClient(handler), deviceIdService);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task LoadSeasonsAsync_PathMismatchFallsBackToEmbyPath(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}")
                : CreateSeasonsResponse()));
        var service = CreateService(handler);

        var result = await service.LoadSeasonsAsync(
            CreateSession(),
            "series-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Shows/series-1/Seasons", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Shows/series-1/Seasons", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_UnauthorizedDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized, "{\"Message\":\"unauthorized\"}")));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SeriesLoadError.Unauthorized, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_ForbiddenDoesNotFallbackOrMasqueradeAsUnauthorized()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Forbidden, "{\"Message\":\"forbidden\"}")));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SeriesLoadError.Forbidden, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("network unavailable"));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SeriesLoadError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadSeasonsAsync_MapsResumeOrUnwatchedFlagAndSortsSeasons()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"season-2\",\"Name\":\"第 2 季\",\"IndexNumber\":2,\"UserData\":{\"UnplayedItemCount\":1}},{\"Id\":\"season-1\",\"Name\":\"第 1 季\",\"IndexNumber\":1}]}")));
        var service = CreateService(handler);

        var result = await service.LoadSeasonsAsync(
            CreateSession(),
            "series-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("season-1", result.Seasons[0].Id);
        Assert.AreEqual("season-2", result.Seasons[1].Id);
        Assert.IsTrue(result.Seasons[1].HasResumeOrUnwatched);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_SortsEpisodesBySeasonAndEpisodeIndex()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"episode-2\",\"Name\":\"Second\",\"IndexNumber\":2,\"ParentIndexNumber\":1},{\"Id\":\"episode-1\",\"Name\":\"First\",\"IndexNumber\":1,\"ParentIndexNumber\":1}]}")));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("episode-1", result.Episodes[0].Id);
        Assert.AreEqual("episode-2", result.Episodes[1].Id);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_MapsPlayedFlagIndependentlyOfPercentage()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"episode-1\",\"IndexNumber\":1,\"ParentIndexNumber\":1,\"UserData\":{\"Played\":true}}]}")));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Episodes[0].IsPlayed);
        Assert.IsNull(result.Episodes[0].PlayedPercentage);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_CleansEpisodeTitleWithoutRepeatingSeriesNameOrEpisodeMarker()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"episode-1\",\"Name\":\"航海王 - S01E1164 - 第 1164 集\",\"SeriesName\":\"航海王\",\"IndexNumber\":1164,\"ParentIndexNumber\":1}]}")));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("第 1164 集", result.Episodes[0].Title);
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_MapsThumbnailAndHeroUrlsFromEncodedPrimaryTagWithoutToken()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"episode-1\",\"ImageTags\":{\"Primary\":\"image tag/+\"}}]}")));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        var thumbnailUrl = result.Episodes[0].ThumbnailUrl;
        var heroImageUrl = result.Episodes[0].HeroImageUrl;
        Assert.IsFalse(string.IsNullOrWhiteSpace(thumbnailUrl));
        Assert.IsFalse(string.IsNullOrWhiteSpace(heroImageUrl));
        Assert.AreEqual(
            "http://media.local:8096/Items/episode-1/Images/Primary?tag=image%20tag%2F%2B&maxWidth=480&quality=90",
            thumbnailUrl);
        Assert.AreEqual(
            "http://media.local:8096/Items/episode-1/Images/Primary?tag=image%20tag%2F%2B&maxWidth=1600&quality=90",
            heroImageUrl);
        Assert.IsFalse(thumbnailUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(thumbnailUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(heroImageUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(heroImageUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadEpisodesAsync_MissingPrimaryTagLeavesThumbnailAndHeroUrlsEmpty()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Items\":[{\"Id\":\"episode-1\",\"ImageTags\":{}}]}")));
        var service = CreateService(handler);

        var result = await service.LoadEpisodesAsync(
            CreateSession(),
            "series-1",
            "season-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsNull(result.Episodes[0].ThumbnailUrl);
        Assert.IsNull(result.Episodes[0].HeroImageUrl);
    }

    private static EmbySeriesService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbySeriesService(
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

    private static HttpResponseMessage CreateSeasonsResponse()
    {
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"Items\":[{\"Id\":\"season-1\",\"Name\":\"第 1 季\",\"IndexNumber\":1}]}");
    }

    private static HttpResponseMessage CreateEpisodesResponse()
    {
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"Items\":[{\"Id\":\"episode-1\",\"Name\":\"单集标题\",\"IndexNumber\":1,\"ParentIndexNumber\":1,\"ImageTags\":{\"Primary\":\"image-tag\"},\"UserData\":{\"PlayedPercentage\":35}}]}");
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
