using System.Net;
using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

public sealed partial class EmbyPlaybackServiceTests
{
    [TestMethod]
    public async Task EpisodePlaybackLoadsSeriesIdentityEvenWithCompleteMarkers()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? CreateResponse(HttpStatusCode.OK,
                    """{"MediaSources":[{"Id":"source-1","Container":"mkv","DirectStreamUrl":"/Videos/item-1/stream.mkv","SupportsDirectStream":true,"RunTimeTicks":6000000000,"MediaStreams":[],"Chapters":[{"StartPositionTicks":100000000,"MarkerType":"IntroStart"},{"StartPositionTicks":200000000,"MarkerType":"IntroEnd"},{"StartPositionTicks":5000000000,"MarkerType":"CreditsStart"}]}]}""")
                : CreateResponse(HttpStatusCode.OK, """{"SeriesId":"series-1","Chapters":[]}""")));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(), CreateRequest(mediaType: "Episode"), CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("series-1", result.PlaybackInfo!.SeriesId);
        Assert.AreEqual(3, result.PlaybackInfo.Markers!.Count);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task MissingSeriesMetadataDoesNotPreventEpisodePlayback()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? CreatePlaybackResponse() : CreateResponse(HttpStatusCode.NotFound, "{}")));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(), CreateRequest(mediaType: "Episode"), CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.PlaybackInfo!.SeriesId);
    }
}
