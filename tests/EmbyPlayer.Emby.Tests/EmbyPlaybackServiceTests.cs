using System.Net;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed partial class EmbyPlaybackServiceTests
{
    [TestMethod]
    public async Task PreparePlaybackAsync_PostsPlaybackInfoEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePlaybackResponse()));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual("/Items/item-1/PlaybackInfo", handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        Assert.AreEqual(
            $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"test-device-id\", Version=\"{ApplicationIdentity.Version}\"",
            handler.Requests[0].EmbyAuthorization);
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[0].Uri.Query), "UserId=user-1");
        StringAssert.Contains(handler.Requests[0].Body, "\"startTimeTicks\":120000000");
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task PreparePlaybackAsync_PostPathOrMethodMismatchTriesGet(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}")
                : CreatePlaybackResponse()));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.AreEqual("/Items/item-1/PlaybackInfo", handler.Requests[1].Uri.AbsolutePath);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task PreparePlaybackAsync_GetPathMismatchFallsBackToEmbyPath(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}"),
                1 => CreateResponse((HttpStatusCode)statusCode, "{\"Message\":\"missing\"}"),
                _ => CreatePlaybackResponse()
            }));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(3, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[2].Method);
        Assert.AreEqual("/emby/Items/item-1/PlaybackInfo", handler.Requests[2].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_UnauthorizedDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized, "{\"Message\":\"unauthorized\"}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackLoadError.Unauthorized, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ForbiddenDoesNotFallbackOrMasqueradeAsUnauthorized()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.Forbidden, "{\"Message\":\"forbidden\"}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackLoadError.Forbidden, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_SendsTokenAndStableDeviceAuthorizationHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePlaybackResponse()));
        var deviceIdService = new TestDeviceIdService("stable-device-id");
        var service = new EmbyPlaybackService(new HttpClient(handler), deviceIdService);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("test-access-token", handler.Requests[0].EmbyToken);
        StringAssert.Contains(handler.Requests[0].EmbyAuthorization, "DeviceId=\"stable-device-id\"");
        Assert.AreEqual(1, deviceIdService.GetCallCount);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ParsesMediaSourcesAndPlaySessionId()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePlaybackResponse()));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("play-session-1", result.PlaybackInfo!.PlaySessionId);
        Assert.AreEqual("source-direct", result.PlaybackInfo.MediaSource.Id);
        Assert.AreEqual("mkv", result.PlaybackInfo.MediaSource.Container);
        Assert.AreEqual(54000000000, result.PlaybackInfo.RunTimeTicks);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ParsesRecognizedChapterMarkersInTimelineOrder()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{"
                + "\"Id\":\"source-direct\",\"Container\":\"mkv\",\"DirectStreamUrl\":\"/Videos/item-1/stream.mkv\","
                + "\"SupportsDirectStream\":true,\"RunTimeTicks\":6000000000,\"MediaStreams\":[],\"Chapters\":["
                + "{\"StartPositionTicks\":5400000000,\"MarkerType\":\"CreditsStart\"},"
                + "{\"StartPositionTicks\":900000000,\"MarkerType\":\"IntroEnd\"},"
                + "{\"StartPositionTicks\":150000000,\"MarkerType\":1},"
                + "{\"StartPositionTicks\":150000000,\"MarkerType\":\"IntroStart\"},"
                + "{\"StartPositionTicks\":100000000,\"MarkerType\":\"Chapter\"},"
                + "{\"StartPositionTicks\":-1,\"MarkerType\":\"CreditsStart\"},"
                + "{\"StartPositionTicks\":7000000000,\"MarkerType\":\"CreditsStart\"}]}]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var markers = result.PlaybackInfo!.Markers!;
        Assert.AreEqual(3, markers.Count);
        Assert.AreEqual(PlaybackMarkerType.IntroStart, markers[0].Type);
        Assert.AreEqual(150000000, markers[0].StartPositionTicks);
        Assert.AreEqual(PlaybackMarkerType.IntroEnd, markers[1].Type);
        Assert.AreEqual(900000000, markers[1].StartPositionTicks);
        Assert.AreEqual(PlaybackMarkerType.CreditsStart, markers[2].Type);
        Assert.AreEqual(5400000000, markers[2].StartPositionTicks);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_LoadsMarkersFromItemWhenPlaybackInfoOmitsChapters()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? CreatePlaybackResponse()
                : CreateResponse(
                    HttpStatusCode.OK,
                    "{\"RunTimeTicks\":54000000000,\"Chapters\":["
                    + "{\"StartPositionTicks\":1920000000,\"MarkerType\":\"IntroStart\"},"
                    + "{\"StartPositionTicks\":2780000000,\"MarkerType\":\"IntroEnd\"},"
                    + "{\"StartPositionTicks\":50000000000,\"MarkerType\":\"CreditsStart\"}]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(mediaType: "Episode"),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("/Users/user-1/Items/item-1", handler.Requests[1].Uri.AbsolutePath);
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[1].Uri.Query), "Fields=Chapters");
        Assert.AreEqual("test-access-token", handler.Requests[1].EmbyToken);
        var markers = result.PlaybackInfo!.Markers!;
        Assert.AreEqual(3, markers.Count);
        Assert.AreEqual(PlaybackMarkerType.IntroStart, markers[0].Type);
        Assert.AreEqual(1920000000, markers[0].StartPositionTicks);
        Assert.AreEqual(PlaybackMarkerType.IntroEnd, markers[1].Type);
        Assert.AreEqual(2780000000, markers[1].StartPositionTicks);
        Assert.AreEqual(PlaybackMarkerType.CreditsStart, markers[2].Type);
        Assert.AreEqual(50000000000, markers[2].StartPositionTicks);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_MergesMissingCreditsWithoutReplacingPlaybackMarkers()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? CreateResponse(
                    HttpStatusCode.OK,
                    "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{"
                    + "\"Id\":\"source-direct\",\"Container\":\"mkv\",\"DirectStreamUrl\":\"/Videos/item-1/stream.mkv\","
                    + "\"SupportsDirectStream\":true,\"RunTimeTicks\":6000000000,\"MediaStreams\":[],\"Chapters\":["
                    + "{\"StartPositionTicks\":100000000,\"MarkerType\":\"IntroStart\"},"
                    + "{\"StartPositionTicks\":800000000,\"MarkerType\":\"IntroEnd\"}]}]}" )
                : CreateResponse(
                    HttpStatusCode.OK,
                    "{\"RunTimeTicks\":6000000000,\"Chapters\":["
                    + "{\"StartPositionTicks\":200000000,\"MarkerType\":\"IntroStart\"},"
                    + "{\"StartPositionTicks\":900000000,\"MarkerType\":\"IntroEnd\"},"
                    + "{\"StartPositionTicks\":5400000000,\"MarkerType\":\"CreditsStart\"}]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(mediaType: "Episode"),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        var markers = result.PlaybackInfo!.Markers!;
        Assert.AreEqual(3, markers.Count);
        Assert.AreEqual(100000000, markers.Single(marker => marker.Type == PlaybackMarkerType.IntroStart).StartPositionTicks);
        Assert.AreEqual(800000000, markers.Single(marker => marker.Type == PlaybackMarkerType.IntroEnd).StartPositionTicks);
        Assert.AreEqual(5400000000, markers.Single(marker => marker.Type == PlaybackMarkerType.CreditsStart).StartPositionTicks);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_MergesMissingIntroWithoutReplacingPlaybackCredits()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? CreateResponse(
                    HttpStatusCode.OK,
                    "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{"
                    + "\"Id\":\"source-direct\",\"Container\":\"mkv\",\"DirectStreamUrl\":\"/Videos/item-1/stream.mkv\","
                    + "\"SupportsDirectStream\":true,\"RunTimeTicks\":6000000000,\"MediaStreams\":[],\"Chapters\":["
                    + "{\"StartPositionTicks\":5300000000,\"MarkerType\":\"CreditsStart\"}]}]}" )
                : CreateResponse(
                    HttpStatusCode.OK,
                    "{\"RunTimeTicks\":6000000000,\"Chapters\":["
                    + "{\"StartPositionTicks\":100000000,\"MarkerType\":\"IntroStart\"},"
                    + "{\"StartPositionTicks\":800000000,\"MarkerType\":\"IntroEnd\"},"
                    + "{\"StartPositionTicks\":5400000000,\"MarkerType\":\"CreditsStart\"}]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(mediaType: "Episode"),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        var markers = result.PlaybackInfo!.Markers!;
        Assert.AreEqual(3, markers.Count);
        Assert.AreEqual(100000000, markers.Single(marker => marker.Type == PlaybackMarkerType.IntroStart).StartPositionTicks);
        Assert.AreEqual(800000000, markers.Single(marker => marker.Type == PlaybackMarkerType.IntroEnd).StartPositionTicks);
        Assert.AreEqual(5300000000, markers.Single(marker => marker.Type == PlaybackMarkerType.CreditsStart).StartPositionTicks);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ItemMarkerFailureDoesNotBlockPlayback()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? CreatePlaybackResponse()
                : CreateResponse(HttpStatusCode.InternalServerError, "{\"Message\":\"failed\"}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(mediaType: "Movie"),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(0, result.PlaybackInfo!.Markers!.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ItemMarkerFailurePreservesExistingMarkers()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                ? CreateResponse(
                    HttpStatusCode.OK,
                    "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{"
                    + "\"Id\":\"source-direct\",\"Container\":\"mkv\",\"DirectStreamUrl\":\"/Videos/item-1/stream.mkv\","
                    + "\"SupportsDirectStream\":true,\"RunTimeTicks\":6000000000,\"MediaStreams\":[],\"Chapters\":["
                    + "{\"StartPositionTicks\":100000000,\"MarkerType\":\"IntroStart\"},"
                    + "{\"StartPositionTicks\":800000000,\"MarkerType\":\"IntroEnd\"}]}]}" )
                : CreateResponse(HttpStatusCode.InternalServerError, "{\"Message\":\"failed\"}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(mediaType: "Episode"),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.PlaybackInfo!.Markers!.Count);
        Assert.IsTrue(result.PlaybackInfo.Markers.Any(marker => marker.Type == PlaybackMarkerType.IntroStart));
        Assert.IsTrue(result.PlaybackInfo.Markers.Any(marker => marker.Type == PlaybackMarkerType.IntroEnd));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ItemMarkerDeviceFailurePreservesPreparedMedia()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{"
                + "\"Id\":\"source-direct\",\"Container\":\"mkv\",\"DirectStreamUrl\":\"/Videos/item-1/stream.mkv\","
                + "\"SupportsDirectStream\":true,\"RunTimeTicks\":6000000000,\"MediaStreams\":[],\"Chapters\":["
                + "{\"StartPositionTicks\":100000000,\"MarkerType\":\"IntroStart\"},"
                + "{\"StartPositionTicks\":800000000,\"MarkerType\":\"IntroEnd\"}]}]}")));
        var deviceIdService = new TestDeviceIdService("test-device-id")
        {
            GetOrCreateDeviceIdAsyncHandler = (callCount, _) => callCount == 2
                ? Task.FromException<string>(new InvalidOperationException("device storage unavailable"))
                : Task.FromResult("test-device-id")
        };
        var service = CreateService(handler, deviceIdService);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(mediaType: "Episode"),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(2, deviceIdService.GetCallCount);
        Assert.AreEqual(2, result.PlaybackInfo!.Markers!.Count);
        Assert.IsTrue(result.PlaybackInfo.Markers.Any(marker => marker.Type == PlaybackMarkerType.IntroStart));
        Assert.IsTrue(result.PlaybackInfo.Markers.Any(marker => marker.Type == PlaybackMarkerType.IntroEnd));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_PrefersDirectStreamMediaSource()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePlaybackResponse()));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("source-direct", result.PlaybackInfo!.MediaSource.Id);
        Assert.IsFalse(result.PlaybackInfo.RequiresTranscoding);
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath!.Contains("/Videos/item-1/stream.mkv", StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_FallsBackToTranscodingUrl()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{\"Id\":\"source-transcode\",\"Container\":\"ts\",\"TranscodingUrl\":\"/Videos/item-1/master.m3u8\",\"SupportsTranscoding\":true,\"MediaStreams\":[]}]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("source-transcode", result.PlaybackInfo!.MediaSource.Id);
        Assert.IsTrue(result.PlaybackInfo.RequiresTranscoding);
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath!.Contains("/Videos/item-1/master.m3u8", StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_ParsesAudioAndSubtitleStreams()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePlaybackResponse()));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.PlaybackInfo!.AudioTracks.Count);
        Assert.AreEqual(1, result.PlaybackInfo.Subtitles.Count);
        Assert.AreEqual(1, result.PlaybackInfo.AudioTracks[0].Index);
        Assert.AreEqual("jpn", result.PlaybackInfo.AudioTracks[0].Language);
        Assert.AreEqual("Japanese AAC 2.0", result.PlaybackInfo.AudioTracks[0].DisplayTitle);
        Assert.AreEqual(2, result.PlaybackInfo.Subtitles[0].Index);
        Assert.IsTrue(result.PlaybackInfo.Subtitles[0].IsExternal);
        Assert.AreEqual("External", result.PlaybackInfo.Subtitles[0].DeliveryMethod);
        Assert.IsTrue(result.PlaybackInfo.Subtitles[0].DeliveryUrl!.Contains(
            "/Videos/item-1/source-direct/Subtitles/2/Stream.srt",
            StringComparison.Ordinal));
        Assert.IsFalse(result.PlaybackInfo.Subtitles[0].DeliveryUrl!.Contains(
            "test-access-token",
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_PreservesRequiredHttpHeadersWithoutAddingTokenToUrl()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePlaybackResponse(addApiKeyToDirectStreamUrl: true)));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.PlaybackInfo!.RequiresTokenInUrl);
        Assert.AreEqual("header-value", result.PlaybackInfo.MediaSource.RequiredHttpHeaders["X-Test-Header"]);
        Assert.IsTrue(result.PlaybackInfo.MediaSource.RequiredHttpHeaders.ContainsKey("X-Emby-Token"));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath!.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("api_key", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_CombinesRelativePlaybackPathWithApiBase()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{\"Id\":\"source-direct\",\"Container\":\"mkv\",\"DirectStreamUrl\":\"Videos/item-1/stream.mkv\",\"SupportsDirectStream\":true,\"MediaStreams\":[]}]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.PlaybackInfo!.PlaybackPath!.StartsWith("http://media.local:8096/", StringComparison.Ordinal));
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath.Contains("/Videos/item-1/stream.mkv", StringComparison.Ordinal));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_GeneratesStreamEndpointWhenPlaybackUrlsAreMissing()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePathOnlyPlaybackResponse()));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("source-path", result.PlaybackInfo!.MediaSource.Id);
        Assert.IsFalse(result.PlaybackInfo.RequiresTranscoding);
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath!.StartsWith("http://media.local:8096/", StringComparison.Ordinal));
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath.Contains("/Videos/item-1/stream?static=true&MediaSourceId=source-path", StringComparison.Ordinal));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("/media/movie", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("Token=", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("header-value", result.PlaybackInfo.MediaSource.RequiredHttpHeaders["X-Test-Header"]);
        Assert.IsTrue(result.PlaybackInfo.MediaSource.RequiredHttpHeaders.ContainsKey("X-Emby-Token"));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_DebugDiagnosticsDoNotChangeOrdinaryStaticStreamOrHeaders()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreatePathOnlyPlaybackResponse()));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(
            "http://media.local:8096/Videos/item-1/stream?static=true&MediaSourceId=source-path",
            result.PlaybackInfo!.PlaybackPath);
        CollectionAssert.AreEquivalent(
            new[] { "X-Emby-Token", "X-Test-Header" },
            result.PlaybackInfo.MediaSource.RequiredHttpHeaders.Keys.ToArray());
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_BlurayStaticProbeSuccessKeepsExistingStaticStream()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index == 0
                ? CreateBlurayPlaybackResponse()
                : CreateResponse(HttpStatusCode.PartialContent, string.Empty)));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.PlaybackInfo!.RequiresTranscoding);
        Assert.AreEqual(
            "http://media.local:8096/Videos/item-1/stream?static=true&MediaSourceId=source-bluray",
            result.PlaybackInfo.PlaybackPath);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.AreEqual("bytes=0-0", handler.Requests[1].Range);
    }

    [DataTestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public async Task PreparePlaybackAsync_BlurayStaticProbeMissingNegotiatesHls(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateBlurayPlaybackResponse(),
                1 => CreateResponse((HttpStatusCode)statusCode, string.Empty),
                _ => CreateBlurayHlsPlaybackResponse()
            }));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.PlaybackInfo!.RequiresTranscoding);
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath!.Contains(
            "/videos/item-1/master.m3u8",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath.Contains("MediaSourceId=source-bluray", StringComparison.Ordinal));
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath.Contains("PlaySessionId=play-session-hls", StringComparison.Ordinal));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEquivalent(
            new[] { "User-Agent", "X-Emby-Authorization", "X-Emby-Token", "X-Test-Header" },
            result.PlaybackInfo.MediaSource.RequiredHttpHeaders.Keys.ToArray());
        Assert.AreEqual(
            $"{ApplicationIdentity.Name}/{ApplicationIdentity.Version}",
            result.PlaybackInfo.MediaSource.RequiredHttpHeaders["User-Agent"]);
        Assert.AreEqual("test-access-token", result.PlaybackInfo.MediaSource.RequiredHttpHeaders["X-Emby-Token"]);

        Assert.AreEqual(3, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[2].Method);
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[2].Uri.Query), "IsPlayback=true");
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[2].Uri.Query), "AutoOpenLiveStream=true");
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests[2].Uri.Query), "MediaSourceId=source-bluray");
        Assert.IsFalse(handler.Requests[2].Uri.Query.Contains("X-Emby-Token", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(handler.Requests[2].Body, "\"deviceProfile\"");
        StringAssert.Contains(handler.Requests[2].Body, "\"protocol\":\"hls\"");
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_BlurayHlsPreservesMarkersFromInitialResponse()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateBlurayPlaybackResponse(includeMarkers: true),
                1 => CreateResponse(HttpStatusCode.NotFound, string.Empty),
                _ => CreateBlurayHlsPlaybackResponse()
            }));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(3, result.PlaybackInfo!.Markers!.Count);
        Assert.AreEqual(PlaybackMarkerType.IntroStart, result.PlaybackInfo.Markers[0].Type);
        Assert.AreEqual(PlaybackMarkerType.IntroEnd, result.PlaybackInfo.Markers[1].Type);
        Assert.AreEqual(PlaybackMarkerType.CreditsStart, result.PlaybackInfo.Markers[2].Type);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_BlurayHlsPreservesInitialAndSupplementedMarkers()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateResponse(
                    HttpStatusCode.OK,
                    "{\"PlaySessionId\":\"play-session-static\",\"MediaSources\":[{"
                    + "\"Id\":\"source-bluray\",\"Container\":\"bluray\",\"Protocol\":\"File\","
                    + "\"SupportsDirectPlay\":true,\"SupportsDirectStream\":false,\"SupportsTranscoding\":true,"
                    + "\"RunTimeTicks\":6000000000,\"DefaultAudioStreamIndex\":1,\"MediaStreams\":["
                    + "{\"Index\":0,\"Type\":\"Video\",\"Codec\":\"h264\"},"
                    + "{\"Index\":1,\"Type\":\"Audio\",\"Codec\":\"dts\"}],\"Chapters\":["
                    + "{\"StartPositionTicks\":100000000,\"MarkerType\":\"IntroStart\"},"
                    + "{\"StartPositionTicks\":800000000,\"MarkerType\":\"IntroEnd\"}]}]}"),
                1 => CreateResponse(
                    HttpStatusCode.OK,
                    "{\"RunTimeTicks\":6000000000,\"Chapters\":["
                    + "{\"StartPositionTicks\":200000000,\"MarkerType\":\"IntroStart\"},"
                    + "{\"StartPositionTicks\":900000000,\"MarkerType\":\"IntroEnd\"},"
                    + "{\"StartPositionTicks\":5400000000,\"MarkerType\":\"CreditsStart\"}]}"),
                2 => CreateResponse(HttpStatusCode.NotFound, string.Empty),
                _ => CreateBlurayHlsPlaybackResponse()
            }));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(mediaType: "Episode"),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(4, handler.Requests.Count);
        var playbackInfo = result.PlaybackInfo!;
        StringAssert.Contains(playbackInfo.PlaybackPath, "/master.m3u8");
        Assert.AreEqual("play-session-hls", playbackInfo.PlaySessionId);
        var markers = playbackInfo.Markers!;
        Assert.AreEqual(3, markers.Count);
        Assert.AreEqual(100000000, markers.Single(marker => marker.Type == PlaybackMarkerType.IntroStart).StartPositionTicks);
        Assert.AreEqual(800000000, markers.Single(marker => marker.Type == PlaybackMarkerType.IntroEnd).StartPositionTicks);
        Assert.AreEqual(5400000000, markers.Single(marker => marker.Type == PlaybackMarkerType.CreditsStart).StartPositionTicks);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_BlurayProbeNetworkFailureDoesNotNegotiateHls()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            index == 0
                ? Task.FromResult(CreateBlurayPlaybackResponse())
                : throw new HttpRequestException("probe unavailable"));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackLoadError.ServerUnreachable, result.Error);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_BlurayHlsNegotiationFailureDoesNotFallbackAgain()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateBlurayPlaybackResponse(),
                1 => CreateResponse(HttpStatusCode.NotFound, string.Empty),
                _ => CreateResponse(HttpStatusCode.InternalServerError, "{\"Message\":\"failed\"}")
            }));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackLoadError.ServerError, result.Error);
        Assert.AreEqual(3, handler.Requests.Count);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_BlurayHlsUsesResolvedEmbyApiPrefix()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 or 1 => CreateResponse(HttpStatusCode.NotFound, string.Empty),
                2 => CreateBlurayPlaybackResponse(),
                3 => CreateResponse(HttpStatusCode.NotFound, string.Empty),
                _ => CreateBlurayHlsPlaybackResponse()
            }));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.PlaybackInfo!.PlaybackPath!.StartsWith(
            "http://media.local:8096/emby/videos/item-1/master.m3u8",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("/emby/emby/", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("/emby/Items/item-1/PlaybackInfo", handler.Requests[4].Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_GeneratedStreamEndpointOmitsMediaSourceIdWhenMissing()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                "{\"PlaySessionId\":\"play-session-1\",\"MediaSources\":[{\"Container\":\"mkv\",\"Path\":\"/media/movie/test.mkv\",\"MediaStreams\":[]}]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.PlaybackInfo!.PlaybackPath!.Contains("/Videos/item-1/stream?static=true", StringComparison.Ordinal));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("MediaSourceId=", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("/media/movie", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("api_key", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_GeneratedStreamEndpointUsesEmbyFallbackApiBase()
    {
        var handler = new RecordingHttpMessageHandler((_, index, _) =>
            Task.FromResult(index switch
            {
                0 => CreateResponse(HttpStatusCode.NotFound, "{\"Message\":\"missing\"}"),
                1 => CreateResponse(HttpStatusCode.NotFound, "{\"Message\":\"missing\"}"),
                _ => CreatePathOnlyPlaybackResponse()
            }));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.PlaybackInfo!.PlaybackPath!.StartsWith("http://media.local:8096/emby/", StringComparison.Ordinal));
        Assert.IsTrue(result.PlaybackInfo.PlaybackPath.Contains("/Videos/item-1/stream?static=true&MediaSourceId=source-path", StringComparison.Ordinal));
        Assert.IsFalse(result.PlaybackInfo.PlaybackPath.Contains("/emby/emby/", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_NoMediaSourcesReturnsNoPlayableSource()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, "{\"MediaSources\":[]}")));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackLoadError.NoPlayableMediaSource, result.Error);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_NoMediaSourcesWritesPlaybackDiagnostic()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, "{\"MediaSources\":[]}")));
        var diagnostics = new TestApplicationDiagnostics();
        var service = CreateService(handler, diagnostics: diagnostics);

        await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.AreEqual(1, diagnostics.Events.Count);
        var diagnostic = diagnostics.Events[0];
        Assert.AreEqual("playback", diagnostic.Category);
        Assert.AreEqual("prepare-failed", diagnostic.EventName);
        Assert.AreEqual("stage=response-parse reason=NoPlayableMediaSource", diagnostic.Details);
    }

    [TestMethod]
    public async Task PreparePlaybackAsync_NetworkFailureDoesNotFallback()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("network unavailable"));
        var service = CreateService(handler);

        var result = await service.PreparePlaybackAsync(
            CreateSession(),
            CreateRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaybackLoadError.ServerUnreachable, result.Error);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    private static EmbyPlaybackService CreateService(
        RecordingHttpMessageHandler handler,
        IDeviceIdService? deviceIdService = null,
        IApplicationDiagnostics? diagnostics = null)
    {
        return new EmbyPlaybackService(
            new HttpClient(handler),
            deviceIdService ?? new TestDeviceIdService("test-device-id"),
            diagnostics);
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

    private static PlaybackStartRequest CreateRequest(
        long startPositionTicks = 120000000,
        string? mediaType = null)
    {
        return new PlaybackStartRequest("item-1", "Playback Item", startPositionTicks, mediaType);
    }

    private static HttpResponseMessage CreatePlaybackResponse(bool addApiKeyToDirectStreamUrl = false)
    {
        var addApiKey = addApiKeyToDirectStreamUrl ? "true" : "false";
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"PlaySessionId\":\"play-session-1\","
            + $"\"AddApiKeyToDirectStreamUrl\":{addApiKey},"
            + "\"MediaSources\":["
            + "{\"Id\":\"source-transcode\",\"Container\":\"ts\",\"TranscodingUrl\":\"/Videos/item-1/master.m3u8\",\"SupportsTranscoding\":true,\"MediaStreams\":[]},"
            + "{\"Id\":\"source-direct\",\"Container\":\"mkv\",\"DirectStreamUrl\":\"/Videos/item-1/stream.mkv?Static=true\",\"SupportsDirectStream\":true,\"SupportsDirectPlay\":true,\"RunTimeTicks\":54000000000,"
            + "\"RequiredHttpHeaders\":{\"X-Test-Header\":\"header-value\"},"
            + "\"MediaStreams\":["
            + "{\"Index\":1,\"Type\":\"Audio\",\"Language\":\"jpn\",\"Codec\":\"aac\",\"DisplayTitle\":\"Japanese AAC 2.0\",\"IsDefault\":true},"
            + "{\"Index\":2,\"Type\":\"Subtitle\",\"Language\":\"chi\",\"Codec\":\"srt\",\"DisplayTitle\":\"中文 SRT\",\"IsExternal\":true,\"DeliveryMethod\":\"External\"}"
            + "]}]}");
    }

    private static HttpResponseMessage CreatePathOnlyPlaybackResponse()
    {
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"PlaySessionId\":\"play-session-1\","
            + "\"MediaSources\":["
            + "{\"Id\":\"source-path\",\"Container\":\"mkv\",\"Path\":\"/media/movie/test.mkv\","
            + "\"RequiredHttpHeaders\":{\"X-Test-Header\":\"header-value\"},"
            + "\"MediaStreams\":[]}"
            + "]}");
    }

    private static HttpResponseMessage CreateBlurayPlaybackResponse(bool includeMarkers = false)
    {
        var chapters = includeMarkers
            ? ",\"Chapters\":["
                + "{\"StartPositionTicks\":100000000,\"MarkerType\":\"IntroStart\"},"
                + "{\"StartPositionTicks\":800000000,\"MarkerType\":\"IntroEnd\"},"
                + "{\"StartPositionTicks\":5000000000,\"MarkerType\":\"CreditsStart\"}]"
            : string.Empty;
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"PlaySessionId\":\"play-session-static\","
            + "\"MediaSources\":[{"
            + "\"Id\":\"source-bluray\",\"Container\":\"bluray\",\"Protocol\":\"File\","
            + "\"SupportsDirectPlay\":true,\"SupportsDirectStream\":false,\"SupportsTranscoding\":true,"
            + "\"DefaultAudioStreamIndex\":1,"
            + "\"MediaStreams\":["
            + "{\"Index\":0,\"Type\":\"Video\",\"Codec\":\"h264\"},"
            + "{\"Index\":1,\"Type\":\"Audio\",\"Codec\":\"dts\"}]"
            + chapters
            + "}]}" );
    }

    private static HttpResponseMessage CreateBlurayHlsPlaybackResponse()
    {
        return CreateResponse(
            HttpStatusCode.OK,
            "{\"PlaySessionId\":\"play-session-hls\","
            + "\"MediaSources\":[{"
            + "\"Id\":\"source-bluray\",\"Container\":\"bluray\","
            + "\"TranscodingUrl\":\"/videos/item-1/master.m3u8?api_key=test-access-token&MediaSourceId=source-bluray&PlaySessionId=play-session-hls&VideoCodec=h264&AudioCodec=aac&SegmentContainer=ts\","
            + "\"TranscodingContainer\":\"ts\",\"TranscodingSubProtocol\":\"hls\","
            + "\"SupportsTranscoding\":true,"
            + "\"RequiredHttpHeaders\":{\"X-Test-Header\":\"header-value\"},"
            + "\"MediaStreams\":[]}]}");
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Emby-Token", out var tokenValues);
            request.Headers.TryGetValues("X-Emby-Authorization", out var authorizationValues);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RequestSnapshot(
                request.RequestUri!,
                request.Method,
                tokenValues?.Single() ?? string.Empty,
                authorizationValues?.Single() ?? string.Empty,
                body,
                request.Headers.Range?.ToString() ?? string.Empty));

            return await handleRequest(request, Requests.Count - 1, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        HttpMethod Method,
        string EmbyToken,
        string EmbyAuthorization,
        string Body,
        string Range);

    private sealed class TestDeviceIdService : IDeviceIdService
    {
        private readonly string deviceId;

        public TestDeviceIdService(string deviceId)
        {
            this.deviceId = deviceId;
        }

        public int GetCallCount { get; private set; }

        public Func<int, CancellationToken, Task<string>>? GetOrCreateDeviceIdAsyncHandler { get; set; }

        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
        {
            GetCallCount++;
            return GetOrCreateDeviceIdAsyncHandler?.Invoke(GetCallCount, cancellationToken)
                ?? Task.FromResult(deviceId);
        }
    }
}
