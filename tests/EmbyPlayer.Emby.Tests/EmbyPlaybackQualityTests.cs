using System.Net;
using System.Text.Json;
using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

public sealed partial class EmbyPlaybackServiceTests
{
    [DataTestMethod]
    [DataRow(PlaybackQuality.FullHd1080, 8_000_000L, 1920, 1080)]
    [DataRow(PlaybackQuality.Hd720, 4_000_000L, 1280, 720)]
    [DataRow(PlaybackQuality.Sd480, 1_500_000L, 854, 480)]
    public async Task PreparePlaybackAsync_QualityNegotiatesCappedHlsAndPreservesSourceAndTracks(
        PlaybackQuality quality, long bitrate, int width, int height)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateQualityResponse()));
        var request = CreateRequest() with
        {
            Quality = quality, MediaSourceId = "source-1", AudioStreamIndex = 3, SubtitleStreamIndex = -1
        };

        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(), request, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var sent = handler.Requests.Single();
        StringAssert.Contains(sent.Uri.Query, "StartTimeTicks=120000000");
        StringAssert.Contains(sent.Uri.Query, "MediaSourceId=source-1");
        StringAssert.Contains(sent.Uri.Query, "AudioStreamIndex=3");
        StringAssert.Contains(sent.Uri.Query, "SubtitleStreamIndex=-1");
        StringAssert.Contains(sent.Uri.Query, $"MaxStreamingBitrate={bitrate}");
        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.IsFalse(root.GetProperty("enableDirectPlay").GetBoolean());
        Assert.IsFalse(root.GetProperty("enableDirectStream").GetBoolean());
        Assert.IsTrue(root.GetProperty("enableTranscoding").GetBoolean());
        Assert.IsFalse(root.GetProperty("allowVideoStreamCopy").GetBoolean());
        Assert.AreEqual(bitrate, root.GetProperty("maxStreamingBitrate").GetInt64());
        Assert.AreEqual(120000000L, root.GetProperty("startTimeTicks").GetInt64());
        Assert.AreEqual("source-1", root.GetProperty("mediaSourceId").GetString());
        Assert.AreEqual(3, root.GetProperty("audioStreamIndex").GetInt32());
        Assert.AreEqual(-1, root.GetProperty("subtitleStreamIndex").GetInt32());
        var profile = root.GetProperty("deviceProfile");
        Assert.AreEqual(bitrate, profile.GetProperty("maxStreamingBitrate").GetInt64());
        Assert.AreEqual(0, profile.GetProperty("directPlayProfiles").GetArrayLength());
        var transcode = profile.GetProperty("transcodingProfiles")[0];
        Assert.AreEqual("hls", transcode.GetProperty("protocol").GetString());
        Assert.AreEqual("h264", transcode.GetProperty("videoCodec").GetString());
        Assert.AreEqual(width, transcode.GetProperty("maxWidth").GetInt32());
        Assert.AreEqual(height, transcode.GetProperty("maxHeight").GetInt32());
        Assert.IsFalse(transcode.GetProperty("copyTimestamps").GetBoolean());
        var info = result.PlaybackInfo!;
        Assert.AreEqual("http://media.local:8096/Videos/item-1/master.m3u8?PlaySessionId=quality-session&VideoCodec=h264&AudioCodec=aac", info.PlaybackPath);
        Assert.AreEqual("quality-session", info.PlaySessionId);
        Assert.AreEqual(quality, info.Quality);
        Assert.AreEqual(120000000L, info.StartPositionTicks);
        Assert.AreEqual(3, info.SelectedAudioStreamIndex);
        Assert.AreEqual(-1, info.SelectedSubtitleStreamIndex);
        Assert.AreEqual(PlaybackMethod.Transcode, info.MediaInfo!.Method);
        Assert.IsTrue(info.RequiresTranscoding);
        Assert.AreEqual(3, info.Markers!.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ReturnsSourceMetadataAndSelectedAudioWithoutUsingQualityCaps()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateQualityResponse()));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(),
            CreateRequest() with { Quality = PlaybackQuality.Sd480, AudioStreamIndex = 3 }, CancellationToken.None);

        var info = result.PlaybackInfo!;
        Assert.AreEqual(new PlaybackMediaInfo(PlaybackMethod.Transcode, 3840, 2160, "hevc",
            35_000_000, 34_000_000, 23.976, "aac", 2), info.MediaInfo);
        Assert.AreEqual(2, info.SelectedSubtitleStreamIndex);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_OriginalSwitchRemovesCapsAndKeepsTheChosenSource()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateQualityResponse()));
        var service = CreateService(handler);
        var request = CreateRequest() with { Quality = PlaybackQuality.Hd720, MediaSourceId = "source-1" };
        var capped = await service.PreparePlaybackAsync(CreateSession(), request, CancellationToken.None);
        var original = await service.PreparePlaybackAsync(CreateSession(), request with
        {
            Quality = PlaybackQuality.Original, StartPositionTicks = 500_000_000,
            AudioStreamIndex = capped.PlaybackInfo!.SelectedAudioStreamIndex,
            SubtitleStreamIndex = capped.PlaybackInfo.SelectedSubtitleStreamIndex
        }, CancellationToken.None);

        Assert.IsTrue(original.IsSuccess);
        Assert.AreEqual(PlaybackQuality.Original, original.PlaybackInfo!.Quality);
        Assert.AreEqual(PlaybackMethod.DirectPlay, original.PlaybackInfo.MediaInfo!.Method);
        Assert.AreEqual("source-1", original.PlaybackInfo.MediaSource.Id);
        Assert.AreEqual(500_000_000L, original.PlaybackInfo.StartPositionTicks);
        Assert.IsFalse(original.PlaybackInfo.RequiresTranscoding);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.IsTrue(body.RootElement.GetProperty("enableDirectPlay").GetBoolean());
        Assert.IsFalse(body.RootElement.TryGetProperty("maxStreamingBitrate", out _));
        Assert.IsFalse(body.RootElement.TryGetProperty("deviceProfile", out _));
        Assert.IsFalse(handler.Requests[1].Uri.Query.Contains("MaxStreamingBitrate", StringComparison.OrdinalIgnoreCase));
    }

    [DataTestMethod]
    [DataRow("{\"MediaSources\":[]}")]
    [DataRow("{\"MediaSources\":[{\"Id\":\"source-1\",\"DirectStreamUrl\":\"/stream.mkv\",\"SupportsDirectPlay\":true}]}")]
    [DataRow("{\"MediaSources\":[{\"Id\":\"different-source\",\"TranscodingUrl\":\"/master.m3u8\"}]}")]
    public async Task PreparePlaybackAsync_QualityWithoutNegotiatedPathForRequestedSourceFails(string json)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, json)));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(),
            CreateRequest() with { Quality = PlaybackQuality.Hd720, MediaSourceId = "source-1" }, CancellationToken.None);

        Assert.AreEqual(PlaybackLoadError.NoPlayableMediaSource, result.Error);
        Assert.IsNull(result.PlaybackInfo);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task PreparePlaybackAsync_QualityPrefixFallbackRetainsPostProfile(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) => Task.FromResult(index == 0
            ? CreateResponse((HttpStatusCode)statusCode, "{}") : CreateQualityResponse()));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(),
            CreateRequest() with { Quality = PlaybackQuality.Hd720 }, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.IsTrue(handler.Requests.All(request => request.Method == HttpMethod.Post));
        Assert.AreEqual("/emby/Items/item-1/PlaybackInfo", handler.Requests[1].Uri.AbsolutePath);
        Assert.AreEqual(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [DataTestMethod]
    [DataRow(401, PlaybackLoadError.Unauthorized)]
    [DataRow(403, PlaybackLoadError.Forbidden)]
    [DataRow(500, PlaybackLoadError.ServerError)]
    public async Task PreparePlaybackAsync_QualityServerFailureReturnsNoReplacement(int statusCode, PlaybackLoadError error)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse((HttpStatusCode)statusCode, "{}")));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(),
            CreateRequest() with { Quality = PlaybackQuality.Hd720 }, CancellationToken.None);

        Assert.AreEqual(error, result.Error);
        Assert.IsNull(result.PlaybackInfo);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_QualityCancellationReturnsNoReplacement()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHttpMessageHandler(async (_, _, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return CreateQualityResponse();
        });
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(),
            CreateRequest() with { Quality = PlaybackQuality.Hd720 }, cancellation.Token);

        Assert.AreEqual(PlaybackLoadError.Cancelled, result.Error);
        Assert.IsNull(result.PlaybackInfo);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_MissingMetadataRemainsUnknown()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK,
            "{\"MediaSources\":[{\"Id\":\"source-1\",\"TranscodingUrl\":\"/master.m3u8\"}]}")));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(),
            CreateRequest() with { Quality = PlaybackQuality.Hd720 }, CancellationToken.None);

        Assert.AreEqual(new PlaybackMediaInfo(PlaybackMethod.Transcode), result.PlaybackInfo!.MediaInfo);
        Assert.IsNull(result.PlaybackInfo.SelectedAudioStreamIndex);
        Assert.IsNull(result.PlaybackInfo.SelectedSubtitleStreamIndex);
    }

    [DataTestMethod]
    [DataRow("StartTimeTicks=120000000&CopyTimestamps=false", 120000000L)]
    [DataRow("StartTimeTicks=120000000&CopyTimestamps=true", 0L)]
    [DataRow("StartTimeTicks=120000000", 120000000L)]
    [DataRow("CopyTimestamps=false", 0L)]
    [DataRow("StartTimeTicks=120000000&CopyTimestamps=invalid", 0L)]
    [DataRow("StartTimeTicks=-1&CopyTimestamps=false", 0L)]
    [DataRow("StartTimeTicks=9223372036854775808&CopyTimestamps=false", 0L)]
    [DataRow("StartTimeTicks=54000000001&CopyTimestamps=false", 0L)]
    [DataRow("StartTimeTicks=oops&CopyTimestamps=false", 0L)]
    public async Task PreparePlaybackAsync_StreamOffsetRequiresValidServerTimeline(string query, long expected)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                MediaSources = new[] { new { Id = "source-1", RunTimeTicks = 54000000000L,
                    TranscodingUrl = "/master.m3u8?" + query } }
            }))));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(),
            CreateRequest() with { Quality = PlaybackQuality.Hd720 }, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(expected, result.PlaybackInfo!.StreamPositionOffsetTicks);
        Assert.AreEqual(120000000L, result.PlaybackInfo.StartPositionTicks);
    }

    [DataTestMethod]
    [DataRow("/stream.mkv?Static=true", false, PlaybackMethod.DirectPlay)]
    [DataRow("/stream.mkv", false, PlaybackMethod.DirectStream)]
    [DataRow("/master.m3u8?VideoCodec=copy&AudioCodec=copy", true, PlaybackMethod.DirectStream)]
    [DataRow("/master.m3u8?VideoCodec=copy&AudioCodec=aac", true, PlaybackMethod.Transcode)]
    public async Task PreparePlaybackAsync_PlayMethodDescribesChosenPath(string path, bool transcode, PlaybackMethod expected)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                MediaSources = new[] { new { Id = "source-1", SupportsDirectPlay = true, SupportsDirectStream = true,
                    DirectStreamUrl = transcode ? null : path, TranscodingUrl = transcode ? path : null } }
            }))));
        var result = await CreateService(handler).PreparePlaybackAsync(CreateSession(), CreateRequest(), CancellationToken.None);

        Assert.AreEqual(expected, result.PlaybackInfo!.MediaInfo!.Method);
    }

    private static HttpResponseMessage CreateQualityResponse() => CreateResponse(HttpStatusCode.OK, """
        {
          "PlaySessionId": "quality-session",
          "MediaSources": [{
            "Id": "source-1", "Container": "mkv", "SupportsDirectPlay": true, "SupportsDirectStream": true,
            "SupportsTranscoding": true, "RunTimeTicks": 54000000000, "Bitrate": 35000000,
            "DirectStreamUrl": "/Videos/item-1/stream.mkv?Static=true",
            "TranscodingUrl": "/Videos/item-1/master.m3u8?PlaySessionId=quality-session&VideoCodec=h264&AudioCodec=aac",
            "DefaultAudioStreamIndex": 1, "DefaultSubtitleStreamIndex": 2,
            "MediaStreams": [
              {"Index":0,"Type":"Video","Width":3840,"Height":2160,"Codec":"hevc","BitRate":34000000,"AverageFrameRate":23.976},
              {"Index":1,"Type":"Audio","Codec":"dts","Channels":6,"IsDefault":true},
              {"Index":2,"Type":"Subtitle","Codec":"srt","IsDefault":true},
              {"Index":3,"Type":"Audio","Codec":"aac","Channels":2}
            ],
            "Chapters": [
              {"MarkerType":"IntroStart","StartPositionTicks":10000000},
              {"MarkerType":"IntroEnd","StartPositionTicks":30000000},
              {"MarkerType":"CreditsStart","StartPositionTicks":50000000000}
            ]
          }]
        }
        """);
}
