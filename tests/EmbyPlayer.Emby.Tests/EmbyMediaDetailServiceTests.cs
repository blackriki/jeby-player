using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Devices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyMediaDetailServiceTests
{
    [TestMethod]
    public async Task LoadDetailAsync_RequestsUserItemEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateDetailResponse("item-1", "Movie", "Detail Movie")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
        Assert.AreEqual("/Users/user-1/Items/item-1", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual(string.Empty, handler.Requests[0].Uri.Query);
    }

    [TestMethod]
    public async Task LoadDetailAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateDetailResponse("item-1", "Movie", "Detail Movie")));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbyMediaDetailService(new HttpClient(handler), deviceIdService);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [TestMethod]
    public async Task LoadDetailAsync_EpisodeUsesFriendlySeriesTitle()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Id\":\"episode-1\",\"Name\":\"Episode Name\",\"Type\":\"Episode\",\"SeriesName\":\"Test Series\",\"ParentIndexNumber\":1,\"IndexNumber\":6}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "episode-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Test Series S1:E6 Episode Name", result.Detail!.Title);
    }

    [TestMethod]
    public async Task LoadDetailAsync_EpisodeNameWithSeriesAndSeasonEpisode_DoesNotDuplicateTitleParts()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Id\":\"episode-1\",\"Name\":\"航海王 - S01E1164 - 第 1164 集\",\"Type\":\"Episode\",\"SeriesName\":\"航海王\",\"ParentIndexNumber\":1,\"IndexNumber\":1164}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "episode-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("航海王 第 1164 集", result.Detail!.Title);
    }

    [TestMethod]
    public async Task LoadDetailAsync_MovieTitleIsNotChangedByEpisodeRules()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateDetailResponse("movie-1", "Movie", "Movie Title")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Movie Title", result.Detail!.Title);
    }

    [TestMethod]
    public async Task LoadDetailAsync_MapsFavoriteState()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Id\":\"movie-1\",\"Name\":\"Movie Title\",\"Type\":\"Movie\",\"UserData\":{\"IsFavorite\":true}}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Detail!.IsFavorite);
    }

    [TestMethod]
    public async Task LoadDetailAsync_MapsPlayedState()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Id\":\"movie-1\",\"Name\":\"Movie Title\",\"Type\":\"Movie\",\"UserData\":{\"Played\":true}}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Detail!.IsPlayed);
    }

    [TestMethod]
    public async Task LoadDetailAsync_MissingOptionalFieldsDoesNotFail()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, "{\"Id\":\"item-1\",\"Type\":\"Movie\"}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("item-1", result.Detail!.Id);
        Assert.AreEqual(string.Empty, result.Detail.Title);
        Assert.IsNull(result.Detail.Year);
        Assert.AreEqual(0, result.Detail.Genres.Count);
        Assert.AreEqual(0, result.Detail.People.Count);
        Assert.AreEqual(0, result.Detail.ArtworkUrls.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_MovieMapsPeopleAndArtworkFromTheSameDetailResponse()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            """
            {
                "Id": "movie-1",
                "Name": "Movie",
                "Type": "Movie",
                "People": [
                    { "Id": "person-1", "Name": "Lead Actor", "Role": "Lead", "Type": "Actor", "PrimaryImageTag": "face tag/+?" },
                    { "Id": "person-2", "Name": "Director", "Type": "Director" }
                ],
                "BackdropImageTags": [ "wide tag/+", "wide tag/+" ],
                "ImageTags": { "Art": "art tag/?" }
            }
            """)));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var detail = result.Detail!;
        Assert.AreEqual("Movie", detail.Type);
        Assert.AreEqual(2, detail.People.Count);
        Assert.AreEqual("Lead Actor", detail.People[0].Name);
        Assert.AreEqual("Lead", detail.People[0].Role);
        Assert.AreEqual("Actor", detail.People[0].Type);
        Assert.AreEqual(
            "http://media.local:8096/Items/person-1/Images/Primary?tag=face%20tag%2F%2B%3F&maxWidth=240&maxHeight=360&quality=85",
            detail.People[0].ImageUrl);
        Assert.AreEqual("Director", detail.People[1].Name);
        Assert.IsNull(detail.People[1].ImageUrl);
        CollectionAssert.AreEqual(
            new[]
            {
                "http://media.local:8096/Items/movie-1/Images/Backdrop/0?tag=wide%20tag%2F%2B&maxWidth=720&quality=85",
                "http://media.local:8096/Items/movie-1/Images/Art?tag=art%20tag%2F%3F&maxWidth=720&quality=85"
            },
            detail.ArtworkUrls.ToArray());
        Assert.IsFalse(detail.People[0].ImageUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(detail.ArtworkUrls.All(url => !url.Contains("token", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Items/movie-1", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual(string.Empty, handler.Requests[0].Uri.Query);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
    }

    [TestMethod]
    public async Task LoadDetailAsync_MapsPeopleInServerOrderIncludingUnknownTypesAndLimitsTo24()
    {
        var peopleJson = string.Join(",", Enumerable.Range(0, 26).Select(index =>
            $"{{\"Id\":\"person-{index}\",\"Name\":\"Person {index}\",\"Role\":\"Role {index}\",\"Type\":\"{(index == 0 ? "StuntDouble" : "Actor")}\"}}"));
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                $"{{\"Id\":\"series-1\",\"Name\":\"Series\",\"Type\":\"Series\",\"People\":[{peopleJson}]}}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "series-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(24, result.Detail!.People.Count);
        Assert.AreEqual("person-0", result.Detail.People[0].Id);
        Assert.AreEqual("Person 0", result.Detail.People[0].Name);
        Assert.AreEqual("Role 0", result.Detail.People[0].Role);
        Assert.AreEqual("StuntDouble", result.Detail.People[0].Type);
        Assert.AreEqual("person-23", result.Detail.People[^1].Id);
    }

    [TestMethod]
    public async Task LoadDetailAsync_PersonImageUrlEncodesTagUsesThumbnailSizeAndContainsNoToken()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            "{\"Id\":\"series-1\",\"Name\":\"Series\",\"Type\":\"Series\",\"People\":[{\"Id\":\"person /1\",\"Name\":\"Actor\",\"Role\":\"Lead\",\"Type\":\"Actor\",\"PrimaryImageTag\":\"face tag/+?\"},{\"Id\":\"person-2\",\"Name\":\"No Image\",\"Type\":\"Director\"}]}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "series-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(
            "http://media.local:8096/Items/person%20%2F1/Images/Primary?tag=face%20tag%2F%2B%3F&maxWidth=240&maxHeight=360&quality=85",
            result.Detail!.People[0].ImageUrl);
        Assert.IsFalse(result.Detail.People[0].ImageUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(result.Detail.People[1].ImageUrl);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_MapsBackdropAndArtThumbnailsInOrderAndDeduplicatesTags()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            "{\"Id\":\"series-1\",\"Name\":\"Series\",\"Type\":\"Series\",\"BackdropImageTags\":[\"same-tag\",\"wide tag/+\",\"same-tag\"],\"ImageTags\":{\"Art\":\"art tag/?\"}}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "series-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(
            new[]
            {
                "http://media.local:8096/Items/series-1/Images/Backdrop/0?tag=same-tag&maxWidth=720&quality=85",
                "http://media.local:8096/Items/series-1/Images/Backdrop/1?tag=wide%20tag%2F%2B&maxWidth=720&quality=85",
                "http://media.local:8096/Items/series-1/Images/Art?tag=art%20tag%2F%3F&maxWidth=720&quality=85"
            },
            result.Detail!.ArtworkUrls.ToArray());
        Assert.IsTrue(result.Detail.ArtworkUrls.All(url => !url.Contains("token", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_ArtworkIsLimitedTo12()
    {
        var tags = string.Join(",", Enumerable.Range(0, 13).Select(index => $"\"backdrop-{index}\""));
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            $"{{\"Id\":\"series-1\",\"Name\":\"Series\",\"Type\":\"Series\",\"BackdropImageTags\":[{tags}],\"ImageTags\":{{\"Art\":\"art-tag\"}}}}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "series-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(12, result.Detail!.ArtworkUrls.Count);
        StringAssert.Contains(result.Detail.ArtworkUrls[^1], "/Backdrop/11?");
        Assert.IsFalse(result.Detail.ArtworkUrls.Any(url => url.Contains("/Images/Art?", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task LoadDetailAsync_PosterAndBackdropUrlsDoNotContainAccessToken()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"Id\":\"item-1\",\"Name\":\"Movie\",\"Type\":\"Movie\",\"ImageTags\":{\"Primary\":\"primary-tag\"},\"BackdropImageTags\":[\"backdrop-tag\"]}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Detail!.PosterUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Detail.PosterUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Detail.BackdropUrl!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Detail.BackdropUrl.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadDetailAsync_UsesOwnLogoWithoutAdditionalRequestOrTokenInUrl()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            "{\"Id\":\"movie-1\",\"Name\":\"Movie\",\"Type\":\"Movie\",\"ImageTags\":{\"Logo\":\"own tag/+?\"},\"ParentLogoItemId\":\"series-1\",\"ParentLogoImageTag\":\"parent-logo\"}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(
            "http://media.local:8096/Items/movie-1/Images/Logo?tag=own%20tag%2F%2B%3F",
            result.Detail!.LogoUrl);
        Assert.IsFalse(result.Detail.LogoUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_InheritsParentLogoWithoutAdditionalRequest()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            "{\"Id\":\"episode-1\",\"Name\":\"Episode\",\"Type\":\"Episode\",\"ParentLogoItemId\":\"series-1\",\"ParentLogoImageTag\":\"parent tag/+\"}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "episode-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(
            "http://media.local:8096/Items/series-1/Images/Logo?tag=parent%20tag%2F%2B",
            result.Detail!.LogoUrl);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_ChangedLogoTagProducesDifferentCacheUrlWithoutToken()
    {
        var tag = "logo-v1";
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(
            HttpStatusCode.OK,
            $"{{\"Id\":\"movie-1\",\"Name\":\"Movie\",\"Type\":\"Movie\",\"ImageTags\":{{\"Logo\":\"{tag}\"}}}}")));
        var service = CreateService(handler);

        var first = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);
        tag = "logo-v2";
        var second = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);

        Assert.AreNotEqual(first.Detail!.LogoUrl, second.Detail!.LogoUrl);
        StringAssert.EndsWith(first.Detail.LogoUrl!, "?tag=logo-v1");
        StringAssert.EndsWith(second.Detail.LogoUrl!, "?tag=logo-v2");
        Assert.IsFalse(second.Detail.LogoUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_NoLogoMetadataLeavesLogoUrlEmpty()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(
            CreateDetailResponse("movie-1", "Movie", "Movie")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "movie-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Detail!.LogoUrl);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_UnauthorizedMapsToUnauthorizedWithoutFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized, "{\"Message\":\"unauthorized\"}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(MediaDetailLoadError.Unauthorized, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task LoadDetailAsync_ForbiddenDoesNotMasqueradeAsUnauthorizedOrFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Forbidden, "{\"Message\":\"forbidden\"}")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(MediaDetailLoadError.Forbidden, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task LoadDetailAsync_PathMismatchFallsBackToEmbyPath(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}")
                : CreateDetailResponse("item-1", "Movie", "Detail Movie")));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Items/item-1", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("/emby/Users/user-1/Items/item-1", handler.Requests[1].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task LoadDetailAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("network unavailable"));
        var service = CreateService(handler);

        var result = await service.LoadDetailAsync(CreateSession(), "item-1", CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(MediaDetailLoadError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    private static EmbyMediaDetailService CreateService(RecordingHttpMessageHandler handler)
    {
        return new EmbyMediaDetailService(
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

    private static HttpResponseMessage CreateDetailResponse(
        string id,
        string type,
        string name)
    {
        return CreateResponse(
            HttpStatusCode.OK,
            $"{{\"Id\":\"{id}\",\"Name\":\"{name}\",\"Type\":\"{type}\",\"ProductionYear\":2024,\"RunTimeTicks\":54000000000,\"Overview\":\"Overview\",\"Genres\":[\"Drama\"],\"CommunityRating\":8.1,\"ImageTags\":{{\"Primary\":\"image-tag\"}},\"UserData\":{{\"PlayedPercentage\":35}}}}");
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
