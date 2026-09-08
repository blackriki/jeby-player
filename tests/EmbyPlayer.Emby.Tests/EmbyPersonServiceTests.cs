using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.People;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyPersonServiceTests
{
    [TestMethod]
    public async Task Person_UsesAuthenticatedItemDetailAndTokenFreePortrait()
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK,
            """{"Id":"person/1","Name":"演员","Overview":"人物简介","ImageTags":{"Primary":"portrait /1"}}"""));
        var result = await Service(handler).LoadPersonAsync(Session, "person/1", CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("演员", result.Person!.Name);
        Assert.AreEqual("人物简介", result.Person.Overview);
        StringAssert.Contains(handler.Uris.Single().AbsoluteUri, "/Users/user-1/Items/person%2F1");
        Assert.AreEqual("test-token", handler.Tokens.Single());
        StringAssert.Contains(handler.Authorizations.Single(), "DeviceId=\"test-device\"");
        Assert.AreEqual("http://media.local/Items/person%2F1/Images/Primary?tag=portrait%20%2F1&maxHeight=480&quality=85", result.Person.ImageUrl);
        Assert.IsFalse(result.Person.ImageUrl!.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Works_FiltersByPersonAndMapsMetadataWhileCursorCountsAllRows()
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK,
            """{"TotalRecordCount":80,"Items":[{"Id":"m1","Name":"Movie","Type":"Movie","ProductionYear":2025,"RunTimeTicks":1000,"ImageTags":{"Primary":"poster"},"UserData":{"PlaybackPositionTicks":250,"IsFavorite":true,"Played":true}},{"Id":"s1","Name":"Series","Type":"series","UserData":{"PlayedPercentage":45}},null,{"Type":"Movie"},{"Id":"e1","Type":"Episode"}]}"""));
        var result = await Service(handler).LoadWorksAsync(Session, "person &1", 48, 48, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(53, result.NextStartIndex);
        Assert.IsTrue(result.HasMore);
        Assert.AreEqual(2025, result.Items[0].Year);
        Assert.AreEqual(25d, result.Items[0].PlayedPercentage);
        Assert.IsTrue(result.Items[0].IsFavorite);
        Assert.IsTrue(result.Items[0].IsPlayed);
        StringAssert.Contains(result.Items[0].PosterUrl!, "maxHeight=360");
        Assert.AreEqual(45d, result.Items[1].PlayedPercentage);
        Assert.IsNull(result.Items[1].PosterUrl);
        var query = handler.Uris.Single().Query;
        foreach (var expected in new[] { "PersonIds=person%20%261", "Recursive=true", "IncludeItemTypes=Movie,Series",
            "StartIndex=48", "Limit=48", "SortBy=ProductionYear,SortName", "EnableUserData=true", "EnableImages=true" })
            StringAssert.Contains(query, expected);
        Assert.AreEqual("test-token", handler.Tokens.Single());
    }

    [TestMethod]
    public async Task Works_BrowsesBeyondPreviewUntilTotalIsConsumed()
    {
        var handler = new Handler((index, _) => Response(HttpStatusCode.OK,
            "{\"TotalRecordCount\":53,\"Items\":[" + string.Join(',', Enumerable.Range(index == 0 ? 0 : 48, index == 0 ? 48 : 5)
                .Select(i => $"{{\"Id\":\"m{i}\",\"Type\":\"Movie\"}}")) + "]}"));
        var service = Service(handler);
        var first = await service.LoadWorksAsync(Session, "p1", 0, 48, CancellationToken.None);
        var second = await service.LoadWorksAsync(Session, "p1", first.NextStartIndex, 48, CancellationToken.None);
        Assert.AreEqual(48, first.Items.Count);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual("m48", second.Items[0].Id);
        Assert.AreEqual(53, second.NextStartIndex);
        Assert.IsFalse(second.HasMore);
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"Items\":[]}")]
    [DataRow("{\"Items\":[],\"TotalRecordCount\":100}")]
    public async Task Works_EmptySuccessStopsPagingAndClampsQuery(string content)
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK, content));
        var result = await Service(handler).LoadWorksAsync(Session, "p1", -5, 500, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, result.NextStartIndex);
        Assert.IsFalse(result.HasMore);
        StringAssert.Contains(handler.Uris.Single().Query, "StartIndex=0&Limit=100");
    }

    [DataTestMethod]
    [DataRow(false, 404)]
    [DataRow(true, 405)]
    public async Task RootPathMismatchRetriesOnceWithEmbyAndPreservesImageBase(bool works, int status)
    {
        const string item = """{"Id":"p1","Type":"Movie","ImageTags":{"Primary":"tag"}}""";
        var handler = new Handler((index, _) => Response(index == 0 ? (HttpStatusCode)status : HttpStatusCode.OK,
            works ? "{\"Items\":[" + item + "]}" : item));
        var service = Service(handler);
        var image = works
            ? (await service.LoadWorksAsync(Session, "p1", 0, 48, CancellationToken.None)).Items.Single().PosterUrl
            : (await service.LoadPersonAsync(Session, "p1", CancellationToken.None)).Person!.ImageUrl;
        Assert.AreEqual(2, handler.Uris.Count);
        StringAssert.StartsWith(handler.Uris[1].AbsolutePath, "/emby/Users/");
        StringAssert.Contains(image!, "/emby/Items/p1/Images/Primary");
        CollectionAssert.AreEqual(new[] { "test-token", "test-token" }, handler.Tokens.ToArray());
    }

    [DataTestMethod]
    [DataRow(401, PersonLoadError.Unauthorized)]
    [DataRow(403, PersonLoadError.Forbidden)]
    [DataRow(404, PersonLoadError.NotFound)]
    [DataRow(500, PersonLoadError.ServerError)]
    public async Task BothEndpointsMapStatus(int status, PersonLoadError expected)
    {
        var handler = new Handler((_, _) => Response((HttpStatusCode)status, "{}"));
        var service = Service(handler);
        Assert.AreEqual(expected, (await service.LoadPersonAsync(Session, "p1", CancellationToken.None)).Error);
        Assert.AreEqual(expected, (await service.LoadWorksAsync(Session, "p1", 0, 48, CancellationToken.None)).Error);
        Assert.AreEqual(status == 404 ? 4 : 2, handler.Uris.Count);
    }

    [DataTestMethod]
    [DataRow("not-json")]
    [DataRow("null")]
    [DataRow("")]
    public async Task BothEndpointsRejectInvalidResponse(string content)
    {
        var service = Service(new Handler((_, _) => Response(HttpStatusCode.OK, content)));
        Assert.AreEqual(PersonLoadError.InvalidResponse, (await service.LoadPersonAsync(Session, "p1", CancellationToken.None)).Error);
        Assert.AreEqual(PersonLoadError.InvalidResponse, (await service.LoadWorksAsync(Session, "p1", 0, 48, CancellationToken.None)).Error);
    }

    [TestMethod]
    public async Task CancellationAndTransportFailuresHaveDistinctErrors()
    {
        using var cancellation = new CancellationTokenSource();
        var service = Service(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        Assert.AreEqual(PersonLoadError.Cancelled, (await service.LoadPersonAsync(Session, "p1", cancellation.Token)).Error);
        service = Service(new Handler((_, _) => throw new TaskCanceledException()));
        Assert.AreEqual(PersonLoadError.ServerTimeout, (await service.LoadWorksAsync(Session, "p1", 0, 48, CancellationToken.None)).Error);
        service = Service(new Handler((_, _) => throw new HttpRequestException()));
        Assert.AreEqual(PersonLoadError.ServerUnreachable, (await service.LoadPersonAsync(Session, "p1", CancellationToken.None)).Error);
    }

    private static readonly AuthSession Session = new("http://media.local", "test-token", "user-1", "Test User", "test-server");
    private static EmbyPersonService Service(Handler handler) => new(new HttpClient(handler), new DeviceId());
    private static Task<HttpResponseMessage> Response(HttpStatusCode status, string content)
        => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
    private sealed class DeviceId : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult("test-device");
    }
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = new();
        public List<string> Tokens { get; } = new();
        public List<string> Authorizations { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            Tokens.Add(request.Headers.GetValues("X-Emby-Token").Single());
            Authorizations.Add(request.Headers.GetValues("X-Emby-Authorization").Single());
            return response(Uris.Count - 1, cancellationToken);
        }
    }
}
