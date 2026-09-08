using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Series;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyNextEpisodeServiceTests
{
    [TestMethod]
    public async Task GetNextEpisodeAsync_ReturnsNextEpisodeInSameSeason()
    {
        var handler = CreateEpisodeHandler(
            "{\"Items\":[{\"Id\":\"episode-2\",\"Name\":\"第二集\",\"SeriesId\":\"series-1\",\"SeasonId\":\"season-1\",\"ParentIndexNumber\":1,\"IndexNumber\":2,\"ParentLogoItemId\":\"series-1\",\"ParentLogoImageTag\":\"logo tag/+\"},{\"Id\":\"episode-1\",\"Name\":\"第一集\",\"SeriesId\":\"series-1\",\"SeasonId\":\"season-1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}]}");
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.NextEpisode);
        Assert.AreEqual("episode-2", result.NextEpisode.ItemId);
        Assert.AreEqual(1, result.NextEpisode.SeasonNumber);
        Assert.AreEqual(2, result.NextEpisode.EpisodeNumber);
        Assert.AreEqual("series-1", result.NextEpisode.SeriesId);
        Assert.AreEqual("season-1", result.NextEpisode.SeasonId);
        Assert.AreEqual(
            "http://media.local:8096/Items/series-1/Images/Logo?tag=logo%20tag%2F%2B",
            result.NextEpisode.LogoUrl);
        Assert.IsFalse(result.NextEpisode.LogoUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(2, handler.Requests.Count);
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[1].Uri.Query), "ParentLogoItemId");
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[1].Uri.Query), "SeasonId");
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_CrossesIntoNextSeason()
    {
        var handler = CreateEpisodeHandler(
            "{\"Items\":[{\"Id\":\"season-2-episode-1\",\"Name\":\"新一季\",\"SeriesId\":\"series-1\",\"SeasonId\":\"season-2\",\"ParentIndexNumber\":2,\"IndexNumber\":1},{\"Id\":\"episode-1\",\"Name\":\"季终集\",\"SeriesId\":\"series-1\",\"SeasonId\":\"season-1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}]}");
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("season-2-episode-1", result.NextEpisode?.ItemId);
        Assert.AreEqual(2, result.NextEpisode?.SeasonNumber);
        Assert.AreEqual(1, result.NextEpisode?.EpisodeNumber);
        Assert.AreEqual("series-1", result.NextEpisode?.SeriesId);
        Assert.AreEqual("season-2", result.NextEpisode?.SeasonId);
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_LastSeriesEpisodeReturnsNoNextEpisode()
    {
        var handler = CreateEpisodeHandler(
            "{\"Items\":[{\"Id\":\"episode-1\",\"Name\":\"最后一集\",\"ParentIndexNumber\":1,\"IndexNumber\":1}]}");
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.NextEpisode);
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_NonEpisodeDoesNotRequestEpisodeList()
    {
        var handler = new RecordingHandler((_, _, _) => Task.FromResult(JsonResponse(
            "{\"Id\":\"movie-1\",\"Type\":\"Movie\"}")));
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "movie-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.NextEpisode);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_MissingEpisodeFieldsDoesNotRequestEpisodeList()
    {
        var handler = new RecordingHandler((_, _, _) => Task.FromResult(JsonResponse(
            "{\"Id\":\"episode-1\",\"Type\":\"Episode\"}")));
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.NextEpisode);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_SortsBySeasonThenEpisode()
    {
        var handler = CreateEpisodeHandler(
            "{\"Items\":[{\"Id\":\"episode-3\",\"ParentIndexNumber\":2,\"IndexNumber\":1},{\"Id\":\"episode-2\",\"ParentIndexNumber\":1,\"IndexNumber\":2},{\"Id\":\"episode-1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}]}");
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.AreEqual("episode-2", result.NextEpisode?.ItemId);
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_SendsAuthenticatedDeviceHeaders()
    {
        var handler = CreateEpisodeHandler("{\"Items\":[]}");
        var result = await CreateService(handler, "stable-device").GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(handler.Requests.All(request => request.Token == "test-access-token"));
        Assert.IsTrue(handler.Requests.All(request => request.Authorization.Contains(
            "DeviceId=\"stable-device\"",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_UnauthorizedDoesNotFallback()
    {
        var handler = new RecordingHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(NextEpisodeError.Unauthorized, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_ForbiddenDoesNotFallbackOrMasqueradeAsUnauthorized()
    {
        var handler = new RecordingHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(NextEpisodeError.Forbidden, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GetNextEpisodeAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHandler((_, _, _) => throw new HttpRequestException("offline"));
        var result = await CreateService(handler).GetNextEpisodeAsync(
            CreateSession(),
            "episode-1",
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(NextEpisodeError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    private static RecordingHandler CreateEpisodeHandler(string episodesJson)
    {
        return new RecordingHandler((_, index, _) => Task.FromResult(index == 0
            ? JsonResponse("{\"Id\":\"episode-1\",\"Type\":\"Episode\",\"SeriesId\":\"series-1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}")
            : JsonResponse(episodesJson)));
    }

    private static EmbyNextEpisodeService CreateService(
        RecordingHandler handler,
        string deviceId = "device-1")
    {
        return new EmbyNextEpisodeService(
            new HttpClient(handler),
            new TestDeviceIdService(deviceId));
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

    private static HttpResponseMessage JsonResponse(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler;

        public RecordingHandler(
            Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        public List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Emby-Token", out var token);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorization);
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                token?.SingleOrDefault() ?? string.Empty,
                authorization?.SingleOrDefault() ?? string.Empty));
            return handler(request, Requests.Count - 1, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(Uri Uri, string Token, string Authorization);

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
