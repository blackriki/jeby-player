using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Emby;

public sealed class EmbyPlaybackService : IPlaybackService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MarkerRequestTimeout = TimeSpan.FromSeconds(3);
    private const long BlurayHlsMaxStaticBitrate = 140_000_000;
    private const long BlurayHlsMaxStreamingBitrate = 140_000_000;
    private const int BlurayHlsMusicBitrate = 192_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IApplicationDiagnostics diagnostics;
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyPlaybackService(
        HttpClient httpClient,
        IDeviceIdService deviceIdService,
        IApplicationDiagnostics? diagnostics = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;
    }

    public async Task<PlaybackLoadResult> PreparePlaybackAsync(
        AuthSession session,
        PlaybackStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ItemId))
        {
            return PlaybackFailure(PlaybackLoadError.NotFound, "request-validation");
        }
        if (!Enum.IsDefined(request.Quality) || request.AudioStreamIndex is < 0 || request.SubtitleStreamIndex is < -1)
        {
            return PlaybackFailure(PlaybackLoadError.InvalidResponse, "request-validation");
        }

        var contentResult = await GetPlaybackInfoWithFallbackAsync(
                session,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        if (!contentResult.IsSuccess)
        {
            return PlaybackFailure(contentResult.Error, "playback-info");
        }

        var parseResult = ParsePlaybackInfo(
            request,
            contentResult.Content!,
            contentResult.ApiBase!,
            session.AccessToken);
        if (!parseResult.IsSuccess)
        {
            return PlaybackFailure(parseResult.Error, "response-parse");
        }

        var playbackInfo = parseResult.PlaybackInfo!;
        if (ShouldLoadItemMarkers(request.MediaType)
            && (!HasAllRequiredMarkers(playbackInfo.Markers)
                || string.Equals(request.MediaType, "Episode", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var itemMarkers = await TryGetItemMarkersAsync(
                        contentResult.ApiBase!,
                        session,
                        request.ItemId,
                        playbackInfo.RunTimeTicks,
                        cancellationToken)
                    .ConfigureAwait(false);
                playbackInfo = playbackInfo with
                {
                    Markers = MergePlaybackMarkers(
                        playbackInfo.Markers,
                        itemMarkers.Markers,
                        playbackInfo.RunTimeTicks),
                    SeriesId = itemMarkers.SeriesId
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return PlaybackFailure(PlaybackLoadError.Cancelled, "marker-enrichment");
            }
            catch (Exception)
            {
                WritePlaybackDiagnostic("item-markers enrichment-failed");
            }
        }

        if (!parseResult.ShouldProbeBlurayStaticStream)
        {
            return PlaybackLoadResult.Success(playbackInfo);
        }

        var probeResult = await ProbeBlurayStaticStreamAsync(
                playbackInfo,
                cancellationToken)
            .ConfigureAwait(false);
        if (probeResult.Error != PlaybackLoadError.None)
        {
            return PlaybackFailure(probeResult.Error, "static-probe");
        }

        if (!probeResult.ShouldUseHls)
        {
            return PlaybackLoadResult.Success(playbackInfo);
        }

        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);
        var hlsResult = await GetBlurayHlsPlaybackInfoAsync(
                contentResult.ApiBase!,
                session,
                request,
                parseResult.SelectedMediaSourceId!,
                deviceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!hlsResult.IsSuccess)
        {
            return PlaybackFailure(hlsResult.Error, "hls-playback-info");
        }

        var hlsParseResult = ParsePlaybackInfo(
            request,
            hlsResult.Content!,
            contentResult.ApiBase!,
            session.AccessToken,
            PlaybackSourceKind.BlurayHls,
            deviceId,
            playbackInfo.Markers);
        return hlsParseResult.IsSuccess
            ? PlaybackLoadResult.Success(hlsParseResult.PlaybackInfo! with { SeriesId = playbackInfo.SeriesId })
            : PlaybackFailure(hlsParseResult.Error, "hls-response-parse");
    }

    private PlaybackLoadResult PlaybackFailure(PlaybackLoadError error, string stage)
    {
        if (error != PlaybackLoadError.Cancelled)
        {
            diagnostics.Write("playback", "prepare-failed", $"stage={stage} reason={error}");
        }

        return PlaybackLoadResult.Failure(error);
    }

    private async Task<(IReadOnlyList<PlaybackMarker> Markers, string? SeriesId)> TryGetItemMarkersAsync(
        string apiBase,
        AuthSession session,
        string itemId,
        long? playbackRunTimeTicks,
        CancellationToken cancellationToken)
    {
        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);
        var endpointUri = BuildEndpointUri(
            apiBase,
            BuildItemMarkersPath(session.UserId, itemId));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(MarkerRequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUri);
            request.Headers.TryAddWithoutValidation("X-Emby-Token", session.AccessToken);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                BuildEmbyAuthorization(deviceId));

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                WritePlaybackDiagnostic(
                    $"item-markers unavailable status={(int)response.StatusCode}");
                return (Array.Empty<PlaybackMarker>(), null);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return (Array.Empty<PlaybackMarker>(), null);
            }

            ItemMarkersResponse? item;
            try
            {
                item = JsonSerializer.Deserialize<ItemMarkersResponse>(content, JsonOptions);
            }
            catch (JsonException)
            {
                WritePlaybackDiagnostic("item-markers invalid-response");
                return (Array.Empty<PlaybackMarker>(), null);
            }

            var markers = MapPlaybackMarkers(
                item?.Chapters,
                item?.RunTimeTicks ?? playbackRunTimeTicks,
                fallbackMarkers: null);
            WritePlaybackDiagnostic($"item-markers loaded count={markers.Count}");
            return (markers, string.IsNullOrWhiteSpace(item?.SeriesId) ? null : item.SeriesId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            WritePlaybackDiagnostic("item-markers timeout");
            return (Array.Empty<PlaybackMarker>(), null);
        }
        catch (HttpRequestException)
        {
            WritePlaybackDiagnostic("item-markers unreachable");
            return (Array.Empty<PlaybackMarker>(), null);
        }
    }

    private static bool ShouldLoadItemMarkers(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() is "movie" or "episode" or "video";
    }

    private static bool HasAllRequiredMarkers(IReadOnlyList<PlaybackMarker>? markers)
    {
        return markers is { Count: > 0 }
            && markers.Any(marker => marker.Type == PlaybackMarkerType.IntroStart)
            && markers.Any(marker => marker.Type == PlaybackMarkerType.IntroEnd)
            && markers.Any(marker => marker.Type == PlaybackMarkerType.CreditsStart);
    }

    private async Task<PlaybackEndpointResult> GetPlaybackInfoWithFallbackAsync(
        AuthSession session,
        PlaybackStartRequest request,
        CancellationToken cancellationToken)
    {
        var serverBase = session.ServerBase.TrimEnd('/');
        var endpointPath = BuildRequestedPlaybackInfoPath(session.UserId, request);
        var primaryResult = await TryApiBaseAsync(
                serverBase,
                endpointPath,
                session,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        if (!primaryResult.CanFallback)
        {
            return primaryResult.WithApiBase(serverBase);
        }

        var embyApiBase = $"{serverBase}/emby";
        var fallbackResult = await TryApiBaseAsync(
                embyApiBase,
                endpointPath,
                session,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        return fallbackResult.WithApiBase(embyApiBase);
    }

    private async Task<PlaybackEndpointResult> TryApiBaseAsync(
        string apiBase,
        string endpointPath,
        AuthSession session,
        PlaybackStartRequest request,
        CancellationToken cancellationToken)
    {
        var endpointUri = BuildEndpointUri(apiBase, endpointPath);
        var postResult = await SendPlaybackInfoAsync(
                HttpMethod.Post,
                endpointUri,
                session.AccessToken,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        // GET cannot carry the required device profile. Do not silently drop quality limits.
        if (postResult.IsSuccess || !postResult.ShouldTryGet || request.Quality != PlaybackQuality.Original)
        {
            return postResult;
        }

        var getResult = await SendPlaybackInfoAsync(
                HttpMethod.Get,
                endpointUri,
                session.AccessToken,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        return getResult;
    }

    private async Task<PlaybackEndpointResult> SendPlaybackInfoAsync(
        HttpMethod method,
        Uri endpointUri,
        string accessToken,
        PlaybackStartRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var httpRequest = new HttpRequestMessage(method, endpointUri);
            httpRequest.Headers.TryAddWithoutValidation("X-Emby-Token", accessToken);
            httpRequest.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                BuildEmbyAuthorization(deviceId));

            if (method == HttpMethod.Post)
            {
                httpRequest.Content = CreateRequestContent(request);
            }

            using var response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return PlaybackEndpointResult.NoFallback(PlaybackLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return PlaybackEndpointResult.NoFallback(PlaybackLoadError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return PlaybackEndpointResult.FallbackAllowed(PlaybackLoadError.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return PlaybackEndpointResult.NoFallback(PlaybackLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(content)
                ? PlaybackEndpointResult.NoFallback(PlaybackLoadError.InvalidResponse)
                : PlaybackEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlaybackEndpointResult.NoFallback(PlaybackLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return PlaybackEndpointResult.NoFallback(PlaybackLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return PlaybackEndpointResult.NoFallback(PlaybackLoadError.ServerUnreachable);
        }
    }

    private async Task<BlurayStaticProbeResult> ProbeBlurayStaticStreamAsync(
        PlaybackInfo playbackInfo,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, playbackInfo.PlaybackPath);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            foreach (var header in playbackInfo.MediaSource.RequiredHttpHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
            {
                return BlurayStaticProbeResult.UseStaticStream();
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return BlurayStaticProbeResult.UseHls();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return BlurayStaticProbeResult.Failure(PlaybackLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return BlurayStaticProbeResult.Failure(PlaybackLoadError.Forbidden);
            }

            return BlurayStaticProbeResult.Failure(PlaybackLoadError.ServerError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BlurayStaticProbeResult.Failure(PlaybackLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return BlurayStaticProbeResult.Failure(PlaybackLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return BlurayStaticProbeResult.Failure(PlaybackLoadError.ServerUnreachable);
        }
    }

    private async Task<PlaybackEndpointResult> GetBlurayHlsPlaybackInfoAsync(
        string apiBase,
        AuthSession session,
        PlaybackStartRequest request,
        string mediaSourceId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var endpointPath = BuildBlurayHlsPlaybackInfoPath(
            session.UserId,
            request,
            mediaSourceId);
        var endpointUri = BuildEndpointUri(apiBase, endpointPath);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri);
            httpRequest.Headers.TryAddWithoutValidation("X-Emby-Token", session.AccessToken);
            httpRequest.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                BuildEmbyAuthorization(deviceId));
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", $"{ApplicationIdentity.Name}/{ApplicationIdentity.Version}");
            httpRequest.Content = CreateBlurayHlsRequestContent();

            using var response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return PlaybackEndpointResult.NoFallback(PlaybackLoadError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return PlaybackEndpointResult.NoFallback(PlaybackLoadError.Forbidden);
            }

            if (!response.IsSuccessStatusCode)
            {
                return PlaybackEndpointResult.NoFallback(PlaybackLoadError.ServerError);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content)
                ? PlaybackEndpointResult.NoFallback(PlaybackLoadError.InvalidResponse)
                : PlaybackEndpointResult.Success(content).WithApiBase(apiBase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlaybackEndpointResult.NoFallback(PlaybackLoadError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return PlaybackEndpointResult.NoFallback(PlaybackLoadError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return PlaybackEndpointResult.NoFallback(PlaybackLoadError.ServerUnreachable);
        }
    }

    private static StringContent CreateRequestContent(PlaybackStartRequest request)
    {
        var limits = GetQualityLimits(request.Quality);
        var body = JsonSerializer.Serialize(
            new PlaybackInfoRequestBody(
                Math.Max(0, request.StartPositionTicks),
                EnableDirectPlay: limits is null,
                EnableDirectStream: limits is null,
                EnableTranscoding: true,
                AllowVideoStreamCopy: limits is null,
                AllowAudioStreamCopy: true,
                request.MediaSourceId,
                request.AudioStreamIndex,
                request.SubtitleStreamIndex,
                limits?.MaxBitrate,
                limits is null ? null : CreateQualityDeviceProfile(limits)),
            JsonOptions);
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static QualityLimits? GetQualityLimits(PlaybackQuality quality) => quality switch
    {
        PlaybackQuality.FullHd1080 => new(8_000_000, 1920, 1080),
        PlaybackQuality.Hd720 => new(4_000_000, 1280, 720),
        PlaybackQuality.Sd480 => new(1_500_000, 854, 480),
        _ => null
    };

    private static PlaybackDeviceProfile CreateQualityDeviceProfile(QualityLimits limits) => new(
        limits.MaxBitrate,
        limits.MaxBitrate,
        BlurayHlsMusicBitrate,
        Array.Empty<object>(),
        new[]
        {
            new PlaybackTranscodingProfile("ts", "Video", "aac", "h264", "Streaming", "hls", "2", "1",
                BreakOnNonKeyFrames: false, ManifestSubtitles: "vtt", limits.MaxWidth, limits.MaxHeight,
                CopyTimestamps: false)
        },
        Array.Empty<object>(),
        Array.Empty<object>(),
        new object[]
        {
            new { Format = "srt", Method = "External" },
            new { Format = "ass", Method = "External" },
            new { Format = "ssa", Method = "External" },
            new { Format = "vtt", Method = "External" }
        });

    private static StringContent CreateBlurayHlsRequestContent()
    {
        var body = JsonSerializer.Serialize(
            new BlurayHlsPlaybackInfoRequestBody(
                new PlaybackDeviceProfile(
                    BlurayHlsMaxStaticBitrate,
                    BlurayHlsMaxStreamingBitrate,
                    BlurayHlsMusicBitrate,
                    Array.Empty<object>(),
                    new[]
                    {
                        new PlaybackTranscodingProfile(
                            "ts",
                            "Video",
                            "mp3,aac",
                            "h264,h265,hevc,av1",
                            "Streaming",
                            "hls",
                            "2",
                            "1",
                            BreakOnNonKeyFrames: false,
                            ManifestSubtitles: "vtt")
                    },
                    Array.Empty<object>(),
                    Array.Empty<object>(),
                    Array.Empty<object>())),
            JsonOptions);
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static PlaybackParseResult ParsePlaybackInfo(
        PlaybackStartRequest request,
        string content,
        string apiBase,
        string accessToken,
        PlaybackSourceKind? sourceKindOverride = null,
        string? deviceId = null,
        IReadOnlyList<PlaybackMarker>? fallbackMarkers = null)
    {
        PlaybackInfoResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<PlaybackInfoResponse>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return PlaybackParseResult.Failure(PlaybackLoadError.InvalidResponse);
        }

        if (response?.MediaSources is null)
        {
            return PlaybackParseResult.Failure(PlaybackLoadError.InvalidResponse);
        }

        var selectedSource = SelectMediaSource(response.MediaSources, request);
        if (selectedSource is null)
        {
            return PlaybackParseResult.Failure(PlaybackLoadError.NoPlayableMediaSource);
        }

        if (sourceKindOverride == PlaybackSourceKind.BlurayHls
            && (string.IsNullOrWhiteSpace(selectedSource.TranscodingUrl)
                || !string.Equals(selectedSource.TranscodingContainer, "ts", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(selectedSource.TranscodingSubProtocol, "hls", StringComparison.OrdinalIgnoreCase)))
        {
            return PlaybackParseResult.Failure(PlaybackLoadError.NoPlayableMediaSource);
        }

        var requiresTranscoding = request.Quality != PlaybackQuality.Original
            || sourceKindOverride == PlaybackSourceKind.BlurayHls;
        var directStreamAvailable = !requiresTranscoding && !string.IsNullOrWhiteSpace(selectedSource.DirectStreamUrl)
            && (selectedSource.SupportsDirectStream == true || selectedSource.SupportsDirectPlay == true);
        var usesTranscodingUrl = !directStreamAvailable
            && !string.IsNullOrWhiteSpace(selectedSource.TranscodingUrl);
        var sourceKind = sourceKindOverride ?? (requiresTranscoding
            ? PlaybackSourceKind.Transcoding
            : GetPlaybackSourceKind(selectedSource));
        var playbackPath = directStreamAvailable
            ? selectedSource.DirectStreamUrl
            : selectedSource.TranscodingUrl ?? BuildStreamEndpointPath(request.ItemId, selectedSource.Id);
        if (sourceKind == PlaybackSourceKind.BlurayHls)
        {
            playbackPath = RemoveSensitivePlaybackQueryParameters(playbackPath);
        }

        playbackPath = NormalizePlaybackPath(playbackPath, apiBase);
        WritePlaybackDiagnostics(request.ItemId, response, selectedSource, playbackPath, sourceKind);

        var mediaSourceId = string.IsNullOrWhiteSpace(selectedSource.Id)
            ? request.ItemId
            : selectedSource.Id!;
        IEnumerable<MediaStreamResponse> mediaStreams = selectedSource.MediaStreams is null
            ? Array.Empty<MediaStreamResponse>()
            : selectedSource.MediaStreams;
        var audioTracks = mediaStreams
            .Where(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            .Select(MapAudioTrack)
            .ToArray();
        var subtitles = mediaStreams
            .Where(stream => string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))
            .Select(stream => MapSubtitle(stream, request.ItemId, mediaSourceId, apiBase))
            .ToArray();
        var audioStreamIndex = request.AudioStreamIndex ?? selectedSource.DefaultAudioStreamIndex
            ?? mediaStreams.FirstOrDefault(stream => stream.IsDefault == true
                && string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase))?.Index;
        var subtitleStreamIndex = request.SubtitleStreamIndex ?? selectedSource.DefaultSubtitleStreamIndex
            ?? mediaStreams.FirstOrDefault(stream => stream.IsDefault == true
                && string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))?.Index;
        var markers = MapPlaybackMarkers(
            selectedSource.Chapters,
            selectedSource.RunTimeTicks,
            fallbackMarkers);

        var requiredHeaders = sourceKind == PlaybackSourceKind.BlurayHls
            ? BuildBlurayHlsHeaders(
                selectedSource.RequiredHttpHeaders,
                accessToken,
                deviceId ?? string.Empty)
            : BuildRequiredHeaders(selectedSource.RequiredHttpHeaders, accessToken);
        var playbackInfo = new PlaybackInfo(
            request.ItemId,
            string.IsNullOrWhiteSpace(request.Title) ? "未命名媒体" : request.Title,
            response.PlaySessionId,
            new PlaybackMediaSource(
                mediaSourceId,
                selectedSource.Container,
                selectedSource.SupportsDirectPlay == true,
                selectedSource.SupportsDirectStream == true,
                selectedSource.SupportsTranscoding == true,
                requiredHeaders),
            playbackPath,
            usesTranscodingUrl || sourceKind == PlaybackSourceKind.BlurayHls,
            sourceKind == PlaybackSourceKind.BlurayHls
                ? false
                : response.AddApiKeyToDirectStreamUrl == true || selectedSource.AddApiKeyToDirectStreamUrl == true,
            selectedSource.RunTimeTicks,
            Math.Max(0, request.StartPositionTicks),
            audioTracks,
            subtitles,
            request.MediaType,
            request.ProductionYear,
            markers,
            request.Quality,
            audioStreamIndex,
            subtitleStreamIndex,
            MapPlaybackMediaInfo(selectedSource, sourceKind, playbackPath, audioStreamIndex),
            usesTranscodingUrl || sourceKind == PlaybackSourceKind.BlurayHls
                ? GetStreamPositionOffsetTicks(playbackPath, selectedSource.RunTimeTicks)
                : 0);
        return PlaybackParseResult.Success(
            playbackInfo,
            selectedSource.Id,
            ShouldProbeBlurayStaticStream(selectedSource, sourceKind));
    }

    private static IReadOnlyList<PlaybackMarker> MapPlaybackMarkers(
        IReadOnlyList<ChapterInfoResponse>? chapters,
        long? runTimeTicks,
        IReadOnlyList<PlaybackMarker>? fallbackMarkers)
    {
        var parsedMarkers = chapters?
            .Select(chapter =>
            {
                var markerType = ParseMarkerType(chapter.MarkerType);
                return markerType.HasValue && chapter.StartPositionTicks.HasValue
                    ? new PlaybackMarker(markerType.Value, chapter.StartPositionTicks.Value)
                    : null;
            })
            .Where(marker => marker is not null
                && marker.StartPositionTicks >= 0
                && (runTimeTicks is not > 0 || marker.StartPositionTicks <= runTimeTicks.Value))
            .Select(marker => marker!)
            .Distinct()
            .OrderBy(marker => marker.StartPositionTicks)
            .ThenBy(marker => marker.Type)
            .ToArray() ?? Array.Empty<PlaybackMarker>();

        return MergePlaybackMarkers(parsedMarkers, fallbackMarkers, runTimeTicks);
    }

    private static IReadOnlyList<PlaybackMarker> MergePlaybackMarkers(
        IReadOnlyList<PlaybackMarker>? primaryMarkers,
        IReadOnlyList<PlaybackMarker>? supplementalMarkers,
        long? runTimeTicks)
    {
        return (primaryMarkers ?? Array.Empty<PlaybackMarker>())
            .Concat(supplementalMarkers ?? Array.Empty<PlaybackMarker>())
            .Where(marker => marker.StartPositionTicks >= 0
                && (runTimeTicks is not > 0 || marker.StartPositionTicks <= runTimeTicks.Value))
            .GroupBy(marker => marker.Type)
            .Select(group => group.First())
            .OrderBy(marker => marker.StartPositionTicks)
            .ThenBy(marker => marker.Type)
            .ToArray();
    }

    private static PlaybackMarkerType? ParseMarkerType(JsonElement markerType)
    {
        if (markerType.ValueKind == JsonValueKind.String)
        {
            return markerType.GetString()?.Trim().ToLowerInvariant() switch
            {
                "introstart" => PlaybackMarkerType.IntroStart,
                "introend" => PlaybackMarkerType.IntroEnd,
                "creditsstart" => PlaybackMarkerType.CreditsStart,
                _ => null
            };
        }

        if (markerType.ValueKind == JsonValueKind.Number && markerType.TryGetInt32(out var numericValue))
        {
            return numericValue switch
            {
                1 => PlaybackMarkerType.IntroStart,
                2 => PlaybackMarkerType.IntroEnd,
                3 => PlaybackMarkerType.CreditsStart,
                _ => null
            };
        }

        return null;
    }

    private static MediaSourceResponse? SelectMediaSource(
        IReadOnlyList<MediaSourceResponse> mediaSources,
        PlaybackStartRequest request)
    {
        var candidates = string.IsNullOrWhiteSpace(request.MediaSourceId)
            ? mediaSources
            : mediaSources.Where(source => string.Equals(source.Id, request.MediaSourceId, StringComparison.Ordinal)).ToArray();
        if (request.Quality != PlaybackQuality.Original)
        {
            // A capped request must use the server's negotiated path, never the static source.
            return candidates.FirstOrDefault(source => !string.IsNullOrWhiteSpace(source.TranscodingUrl));
        }

        return candidates.FirstOrDefault(source =>
                !string.IsNullOrWhiteSpace(source.DirectStreamUrl)
                && (source.SupportsDirectStream == true || source.SupportsDirectPlay == true))
            ?? candidates.FirstOrDefault(source => !string.IsNullOrWhiteSpace(source.TranscodingUrl))
            ?? candidates.FirstOrDefault();
    }

    private static PlaybackMediaInfo MapPlaybackMediaInfo(
        MediaSourceResponse source,
        PlaybackSourceKind sourceKind,
        string? playbackPath,
        int? audioStreamIndex)
    {
        var video = source.MediaStreams?.FirstOrDefault(stream =>
            string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase));
        var audio = source.MediaStreams?.FirstOrDefault(stream => stream.Index == audioStreamIndex
            && string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase));
        var method = sourceKind switch
        {
            PlaybackSourceKind.StaticStream => PlaybackMethod.DirectPlay,
            PlaybackSourceKind.DirectStream => string.Equals(GetPlaybackQueryValue(playbackPath, "Static"), "true", StringComparison.OrdinalIgnoreCase)
                ? PlaybackMethod.DirectPlay
                : PlaybackMethod.DirectStream,
            PlaybackSourceKind.Transcoding or PlaybackSourceKind.BlurayHls =>
                string.Equals(GetPlaybackQueryValue(playbackPath, "VideoCodec"), "copy", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(GetPlaybackQueryValue(playbackPath, "AudioCodec"), "copy", StringComparison.OrdinalIgnoreCase)
                    ? PlaybackMethod.DirectStream
                    : PlaybackMethod.Transcode,
            _ => PlaybackMethod.Unknown
        };
        return new PlaybackMediaInfo(
            method,
            video?.Width is > 0 ? video.Width : null,
            video?.Height is > 0 ? video.Height : null,
            string.IsNullOrWhiteSpace(video?.Codec) ? null : video.Codec,
            source.Bitrate is > 0 ? source.Bitrate : null,
            video?.BitRate is > 0 ? video.BitRate : null,
            video?.AverageFrameRate is > 0 ? video.AverageFrameRate
                : video?.RealFrameRate is > 0 ? video.RealFrameRate : null,
            string.IsNullOrWhiteSpace(audio?.Codec) ? null : audio.Codec,
            audio?.Channels is > 0 ? audio.Channels : null);
    }

    private static string? GetPlaybackQueryValue(string? path, string key)
    {
        var queryStart = path?.IndexOf('?') ?? -1;
        if (queryStart < 0)
        {
            return null;
        }

        foreach (var pair in path![(queryStart + 1)..].Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static long GetStreamPositionOffsetTicks(string? playbackPath, long? runTimeTicks)
    {
        // The HLS API defines CopyTimestamps=false by default. Only the server URL,
        // not StartTimeTicks in the negotiation request, establishes an offset.
        var copyTimestamps = GetPlaybackQueryValue(playbackPath, "CopyTimestamps");
        if ((copyTimestamps is not null && !string.Equals(copyTimestamps, "false", StringComparison.OrdinalIgnoreCase))
            || !long.TryParse(GetPlaybackQueryValue(playbackPath, "StartTimeTicks"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var offset)
            || offset < 0
            || (runTimeTicks is > 0 && offset > runTimeTicks.Value))
        {
            return 0;
        }

        return offset;
    }

    private static void WritePlaybackDiagnostics(
        string itemId,
        PlaybackInfoResponse response,
        MediaSourceResponse selectedSource,
        string? playbackPath,
        PlaybackSourceKind sourceKind)
    {
        try
        {
            var messages = new List<string>
            {
                "playback-info "
#if DEBUG
                + $"itemHash8={CreateDiagnosticHash8(itemId)} "
                + $"hasPlaySessionId={!string.IsNullOrWhiteSpace(response.PlaySessionId)} "
#endif
                + $"mediaSourceCount={response.MediaSources?.Count ?? 0} "
                + $"addApiKeyToDirectStreamUrl={FormatNullableBool(response.AddApiKeyToDirectStreamUrl)}"
            };

            if (response.MediaSources is not null)
            {
                for (var index = 0; index < response.MediaSources.Count; index++)
                {
                    var source = response.MediaSources[index];
                    messages.Add(
                        "media-source "
                        + $"index={index.ToString(CultureInfo.InvariantCulture)} "
#if DEBUG
                        + $"mediaSourceHash8={CreateDiagnosticHash8(source.Id)} "
#endif
                        + $"hasMediaSourceId={!string.IsNullOrWhiteSpace(source.Id)} "
                        + $"container={FormatDiagnosticValue(source.Container)} "
                        + $"videoCodec={FormatDiagnosticValue(JoinStreamCodecs(source, "Video"))} "
                        + $"audioCodec={FormatDiagnosticValue(JoinStreamCodecs(source, "Audio"))} "
                        + $"supportsDirectPlay={FormatNullableBool(source.SupportsDirectPlay)} "
                        + $"supportsDirectStream={FormatNullableBool(source.SupportsDirectStream)} "
                        + $"supportsTranscoding={FormatNullableBool(source.SupportsTranscoding)} "
                        + $"hasDirectStreamUrl={!string.IsNullOrWhiteSpace(source.DirectStreamUrl)} "
                        + $"hasTranscodingUrl={!string.IsNullOrWhiteSpace(source.TranscodingUrl)} "
                        + $"hasRequiredHttpHeaders={source.RequiredHttpHeaders?.Count > 0} "
                        + $"addApiKeyToDirectStreamUrl={FormatNullableBool(source.AddApiKeyToDirectStreamUrl)}"
#if DEBUG
                        + $" protocol={FormatDiagnosticValue(source.Protocol)}"
                        + $" videoType={FormatDiagnosticValue(source.VideoType)}"
                        + $" isoType={FormatDiagnosticValue(source.IsoType)}"
                        + $" requiresOpening={FormatNullableBool(source.RequiresOpening)}"
                        + $" requiresClosing={FormatNullableBool(source.RequiresClosing)}"
                        + $" requiresLooping={FormatNullableBool(source.RequiresLooping)}"
                        + $" hasOpenToken={!string.IsNullOrWhiteSpace(source.OpenToken)}"
                        + $" isRemote={FormatNullableBool(source.IsRemote)}"
                        + $" isInfiniteStream={FormatNullableBool(source.IsInfiniteStream)}"
                        + $" pathType={ClassifyDiagnosticPath(source)}"
                        + $" mediaStreamCount={source.MediaStreams?.Count ?? 0}"
                        + $" hasDefaultVideoTrack={HasDefaultTrack(source, "Video")}"
                        + $" hasDefaultAudioTrack={HasDefaultTrack(source, "Audio")}"
                        + $" extraFieldNames={FormatDiagnosticFieldNames(source.ExtraFields?.Keys)}"
#endif
                    );
                }
            }

            messages.Add(
                "selected-playback "
                + $"sourceType={sourceKind} "
                + $"pathKind={GetPlaybackPathKind(playbackPath)} "
                + $"pathPrefix={GetPlaybackPathPrefix(playbackPath)} "
                + $"headerNames={FormatHeaderNames(sourceKind == PlaybackSourceKind.BlurayHls
                    ? BuildBlurayHlsHeaders(selectedSource.RequiredHttpHeaders, "redacted", "redacted")
                    : BuildRequiredHeaders(selectedSource.RequiredHttpHeaders, "redacted"))}");

            foreach (var message in messages)
            {
                WritePlaybackDiagnostic(message);
            }
        }
        catch
        {
            // Diagnostics must never affect playback.
        }
    }

    private static PlaybackSourceKind GetPlaybackSourceKind(MediaSourceResponse selectedSource)
    {
        var directStreamAvailable = !string.IsNullOrWhiteSpace(selectedSource.DirectStreamUrl)
            && (selectedSource.SupportsDirectStream == true || selectedSource.SupportsDirectPlay == true);
        if (directStreamAvailable)
        {
            return PlaybackSourceKind.DirectStream;
        }

        if (!string.IsNullOrWhiteSpace(selectedSource.TranscodingUrl))
        {
            return PlaybackSourceKind.Transcoding;
        }

        return PlaybackSourceKind.StaticStream;
    }

    private static bool ShouldProbeBlurayStaticStream(
        MediaSourceResponse selectedSource,
        PlaybackSourceKind sourceKind)
    {
        return sourceKind == PlaybackSourceKind.StaticStream
            && string.Equals(selectedSource.Container, "bluray", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(selectedSource.DirectStreamUrl)
            && string.IsNullOrWhiteSpace(selectedSource.TranscodingUrl)
            && !string.IsNullOrWhiteSpace(selectedSource.Id);
    }

    private static string? JoinStreamCodecs(MediaSourceResponse source, string streamType)
    {
        var codecs = source.MediaStreams?
            .Where(stream => string.Equals(stream.Type, streamType, StringComparison.OrdinalIgnoreCase))
            .Select(stream => stream.Codec?.Trim())
            .Where(codec => !string.IsNullOrWhiteSpace(codec))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return codecs is null || codecs.Length == 0
            ? null
            : string.Join(",", codecs);
    }

    private static PlaybackTrack MapAudioTrack(MediaStreamResponse stream)
    {
        return new PlaybackTrack(
            stream.Index,
            GetDisplayText(stream.Language, "未知语言"),
            GetDisplayText(stream.Codec, "未知编码"),
            GetTrackDisplayTitle(stream),
            stream.IsDefault == true);
    }

    private static PlaybackSubtitle MapSubtitle(
        MediaStreamResponse stream,
        string itemId,
        string mediaSourceId,
        string apiBase)
    {
        return new PlaybackSubtitle(
            stream.Index,
            GetDisplayText(stream.Language, "未知语言"),
            GetDisplayText(stream.Codec, "未知编码"),
            GetTrackDisplayTitle(stream),
            stream.IsDefault == true,
            stream.IsExternal == true,
            stream.DeliveryMethod,
            BuildSubtitleDeliveryUrl(stream, itemId, mediaSourceId, apiBase));
    }

    private static string? BuildSubtitleDeliveryUrl(
        MediaStreamResponse stream,
        string itemId,
        string mediaSourceId,
        string apiBase)
    {
        if (!string.IsNullOrWhiteSpace(stream.DeliveryUrl))
        {
            return NormalizePlaybackPath(stream.DeliveryUrl, apiBase);
        }

        if (stream.IsExternal != true)
        {
            return null;
        }

        var extension = GetSubtitleExtension(stream.Codec);
        return NormalizePlaybackPath(
            $"/Videos/{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(mediaSourceId)}/Subtitles/{stream.Index.ToString(CultureInfo.InvariantCulture)}/Stream.{extension}",
            apiBase);
    }

    private static string GetSubtitleExtension(string? codec)
    {
        var normalized = codec?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "ass" => "ass",
            "ssa" => "ssa",
            "vtt" or "webvtt" => "vtt",
            "subrip" or "srt" => "srt",
            _ => string.IsNullOrWhiteSpace(normalized) ? "srt" : normalized!
        };
    }

    private static string GetTrackDisplayTitle(MediaStreamResponse stream)
    {
        if (!string.IsNullOrWhiteSpace(stream.DisplayTitle))
        {
            return stream.DisplayTitle!;
        }

        if (!string.IsNullOrWhiteSpace(stream.Title))
        {
            return stream.Title!;
        }

        return GetDisplayText(stream.Language, "未命名轨道");
    }

    private static string GetDisplayText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value!;
    }

    private static string? NormalizePlaybackPath(string? playbackPath, string apiBase)
    {
        if (string.IsNullOrWhiteSpace(playbackPath))
        {
            return null;
        }

        if (Uri.TryCreate(playbackPath, UriKind.Absolute, out _))
        {
            return playbackPath;
        }

        return $"{apiBase.TrimEnd('/')}/{playbackPath.TrimStart('/')}";
    }

    private static string? RemoveSensitivePlaybackQueryParameters(string? playbackPath)
    {
        if (string.IsNullOrWhiteSpace(playbackPath))
        {
            return playbackPath;
        }

        var queryIndex = playbackPath.IndexOf('?');
        if (queryIndex < 0)
        {
            return playbackPath;
        }

        var safeParts = playbackPath[(queryIndex + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var name = part.Split('=', 2)[0];
                return !name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("token", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("access_token", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        return safeParts.Length == 0
            ? playbackPath[..queryIndex]
            : $"{playbackPath[..queryIndex]}?{string.Join('&', safeParts)}";
    }

    private static string BuildStreamEndpointPath(string itemId, string? mediaSourceId)
    {
        var path = $"/Videos/{Uri.EscapeDataString(itemId)}/stream?static=true";
        return string.IsNullOrWhiteSpace(mediaSourceId)
            ? path
            : $"{path}&MediaSourceId={Uri.EscapeDataString(mediaSourceId)}";
    }

    private static string GetPlaybackPathKind(string? playbackPath)
    {
        if (string.IsNullOrWhiteSpace(playbackPath))
        {
            return "Missing";
        }

        return Uri.TryCreate(playbackPath, UriKind.Absolute, out _) ? "Absolute" : "Relative";
    }

    private static string GetPlaybackPathPrefix(string? playbackPath)
    {
        if (string.IsNullOrWhiteSpace(playbackPath))
        {
            return "missing";
        }

        var path = playbackPath;
        if (Uri.TryCreate(playbackPath, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "/";
        }

        if (parts.Length >= 2 && string.Equals(parts[0], "emby", StringComparison.OrdinalIgnoreCase))
        {
            return $"/{parts[0]}/{parts[1]}";
        }

        return $"/{parts[0]}";
    }

    private static string FormatHeaderNames(IReadOnlyDictionary<string, string> headers)
    {
        return headers.Count == 0
            ? "none"
            : string.Join(
                ",",
                headers.Keys
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    private static string FormatDiagnosticValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
    }

#if DEBUG
    private static string CreateDiagnosticHash8(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static bool HasDefaultTrack(MediaSourceResponse source, string streamType)
    {
        return source.MediaStreams?.Any(stream =>
            string.Equals(stream.Type, streamType, StringComparison.OrdinalIgnoreCase)
            && stream.IsDefault == true) == true;
    }

    private static string ClassifyDiagnosticPath(MediaSourceResponse source)
    {
        var path = source.Path;
        if (!string.IsNullOrWhiteSpace(source.IsoType)
            || string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase))
        {
            return "iso";
        }

        if (string.Equals(source.VideoType, "BluRay", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(path)
                && path.Replace('\\', '/').Contains("/BDMV", StringComparison.OrdinalIgnoreCase)))
        {
            return "bluray-folder";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "unknown";
        }

        if (path.EndsWith('/') || path.EndsWith('\\') || string.IsNullOrWhiteSpace(Path.GetExtension(path)))
        {
            return "directory";
        }

        return "file";
    }

    private static string FormatDiagnosticFieldNames(IEnumerable<string>? fieldNames)
    {
        if (fieldNames is null)
        {
            return "none";
        }

        var names = fieldNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? "none" : string.Join(',', names);
    }
#endif

    private static void WritePlaybackDiagnostic(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmbyPlayer",
                "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "playback.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never affect playback.
        }
    }

    private static IReadOnlyDictionary<string, string> BuildRequiredHeaders(
        IReadOnlyDictionary<string, string>? sourceHeaders,
        string accessToken)
    {
        var headers = new Dictionary<string, string>(
            sourceHeaders ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);

        if (!headers.ContainsKey("X-Emby-Token"))
        {
            headers["X-Emby-Token"] = accessToken;
        }

        return headers;
    }

    private static IReadOnlyDictionary<string, string> BuildBlurayHlsHeaders(
        IReadOnlyDictionary<string, string>? sourceHeaders,
        string accessToken,
        string deviceId)
    {
        var headers = new Dictionary<string, string>(
            BuildRequiredHeaders(sourceHeaders, accessToken),
            StringComparer.OrdinalIgnoreCase)
        {
            ["X-Emby-Authorization"] = BuildEmbyAuthorization(deviceId),
            ["User-Agent"] = $"{ApplicationIdentity.Name}/{ApplicationIdentity.Version}"
        };
        return headers;
    }

    private static string BuildPlaybackInfoPath(string userId, string itemId)
    {
        return $"/Items/{Uri.EscapeDataString(itemId)}/PlaybackInfo"
            + $"?UserId={Uri.EscapeDataString(userId)}";
    }

    private static string BuildRequestedPlaybackInfoPath(string userId, PlaybackStartRequest request)
    {
        var path = BuildPlaybackInfoPath(userId, request.ItemId)
            + $"&StartTimeTicks={Math.Max(0, request.StartPositionTicks).ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(request.MediaSourceId))
        {
            path += $"&MediaSourceId={Uri.EscapeDataString(request.MediaSourceId)}";
        }
        if (request.AudioStreamIndex.HasValue)
        {
            path += $"&AudioStreamIndex={request.AudioStreamIndex.Value.ToString(CultureInfo.InvariantCulture)}";
        }
        if (request.SubtitleStreamIndex.HasValue)
        {
            path += $"&SubtitleStreamIndex={request.SubtitleStreamIndex.Value.ToString(CultureInfo.InvariantCulture)}";
        }
        if (GetQualityLimits(request.Quality) is { } limits)
        {
            path += $"&MaxStreamingBitrate={limits.MaxBitrate.ToString(CultureInfo.InvariantCulture)}&IsPlayback=true";
        }
        return path;
    }

    private static string BuildItemMarkersPath(string userId, string itemId)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items/{Uri.EscapeDataString(itemId)}"
            + "?Fields=Chapters";
    }

    private static string BuildBlurayHlsPlaybackInfoPath(
        string userId,
        PlaybackStartRequest request,
        string mediaSourceId)
    {
        return BuildRequestedPlaybackInfoPath(userId, request with { MediaSourceId = mediaSourceId })
            + "&IsPlayback=true"
            + "&AutoOpenLiveStream=true"
            + $"&MaxStreamingBitrate={BlurayHlsMaxStreamingBitrate.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildEmbyAuthorization(string deviceId)
    {
        return $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"";
    }

    private static Uri BuildEndpointUri(string apiBase, string endpointPath)
    {
        return new Uri($"{apiBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private sealed record PlaybackInfoRequestBody(
        long StartTimeTicks,
        bool EnableDirectPlay,
        bool EnableDirectStream,
        bool EnableTranscoding,
        bool AllowVideoStreamCopy,
        bool AllowAudioStreamCopy,
        string? MediaSourceId,
        int? AudioStreamIndex,
        int? SubtitleStreamIndex,
        long? MaxStreamingBitrate,
        PlaybackDeviceProfile? DeviceProfile);

    private sealed record QualityLimits(long MaxBitrate, int MaxWidth, int MaxHeight);

    private sealed record BlurayHlsPlaybackInfoRequestBody(
        PlaybackDeviceProfile DeviceProfile);

    private sealed record PlaybackDeviceProfile(
        long MaxStaticBitrate,
        long MaxStreamingBitrate,
        int MusicStreamingTranscodingBitrate,
        IReadOnlyList<object> DirectPlayProfiles,
        IReadOnlyList<PlaybackTranscodingProfile> TranscodingProfiles,
        IReadOnlyList<object> ContainerProfiles,
        IReadOnlyList<object> CodecProfiles,
        IReadOnlyList<object> SubtitleProfiles);

    private sealed record PlaybackTranscodingProfile(
        string Container,
        string Type,
        string AudioCodec,
        string VideoCodec,
        string Context,
        string Protocol,
        string MaxAudioChannels,
        string MinSegments,
        bool BreakOnNonKeyFrames,
        string ManifestSubtitles,
        int? MaxWidth = null,
        int? MaxHeight = null,
        bool? CopyTimestamps = null);

    private sealed class PlaybackInfoResponse
    {
        public string? PlaySessionId { get; set; }

        public bool? AddApiKeyToDirectStreamUrl { get; set; }

        public List<MediaSourceResponse>? MediaSources { get; set; }
    }

    private sealed class ItemMarkersResponse
    {
        public string? SeriesId { get; set; }
        public long? RunTimeTicks { get; set; }

        public List<ChapterInfoResponse>? Chapters { get; set; }
    }

    private sealed class MediaSourceResponse
    {
        public string? Id { get; set; }

        public string? Container { get; set; }

#if DEBUG
        public string? Protocol { get; set; }

        public string? VideoType { get; set; }

        public string? IsoType { get; set; }

        public bool? RequiresOpening { get; set; }

        public bool? RequiresClosing { get; set; }

        public bool? RequiresLooping { get; set; }

        public string? OpenToken { get; set; }

        public bool? IsRemote { get; set; }

        public bool? IsInfiniteStream { get; set; }
#endif

        public string? Path { get; set; }

        public string? DirectStreamUrl { get; set; }

        public string? TranscodingUrl { get; set; }

        public string? TranscodingContainer { get; set; }

        public string? TranscodingSubProtocol { get; set; }

        public bool? SupportsDirectPlay { get; set; }

        public bool? SupportsDirectStream { get; set; }

        public bool? SupportsTranscoding { get; set; }

        public bool? AddApiKeyToDirectStreamUrl { get; set; }

        public long? RunTimeTicks { get; set; }

        public long? Bitrate { get; set; }

        public int? DefaultAudioStreamIndex { get; set; }

        public int? DefaultSubtitleStreamIndex { get; set; }

        public Dictionary<string, string>? RequiredHttpHeaders { get; set; }

        public List<MediaStreamResponse>? MediaStreams { get; set; }

        public List<ChapterInfoResponse>? Chapters { get; set; }

#if DEBUG
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtraFields { get; set; }
#endif
    }

    private sealed class ChapterInfoResponse
    {
        public long? StartPositionTicks { get; set; }

        public JsonElement MarkerType { get; set; }
    }

    private sealed class MediaStreamResponse
    {
        public int Index { get; set; }

        public string? Type { get; set; }

        public string? Language { get; set; }

        public string? Codec { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        public long? BitRate { get; set; }

        public double? AverageFrameRate { get; set; }

        public double? RealFrameRate { get; set; }

        public int? Channels { get; set; }

        public string? DisplayTitle { get; set; }

        public string? Title { get; set; }

        public bool? IsDefault { get; set; }

        public bool? IsExternal { get; set; }

        public string? DeliveryMethod { get; set; }

        public string? DeliveryUrl { get; set; }
    }

    private sealed class PlaybackParseResult
    {
        private PlaybackParseResult(
            bool isSuccess,
            PlaybackInfo? playbackInfo,
            PlaybackLoadError error,
            string? selectedMediaSourceId,
            bool shouldProbeBlurayStaticStream)
        {
            IsSuccess = isSuccess;
            PlaybackInfo = playbackInfo;
            Error = error;
            SelectedMediaSourceId = selectedMediaSourceId;
            ShouldProbeBlurayStaticStream = shouldProbeBlurayStaticStream;
        }

        public bool IsSuccess { get; }

        public PlaybackInfo? PlaybackInfo { get; }

        public PlaybackLoadError Error { get; }

        public string? SelectedMediaSourceId { get; }

        public bool ShouldProbeBlurayStaticStream { get; }

        public static PlaybackParseResult Success(
            PlaybackInfo playbackInfo,
            string? selectedMediaSourceId,
            bool shouldProbeBlurayStaticStream)
        {
            return new PlaybackParseResult(
                true,
                playbackInfo,
                PlaybackLoadError.None,
                selectedMediaSourceId,
                shouldProbeBlurayStaticStream);
        }

        public static PlaybackParseResult Failure(PlaybackLoadError error)
        {
            return new PlaybackParseResult(false, null, error, null, false);
        }
    }

    private sealed record BlurayStaticProbeResult(
        bool ShouldUseHls,
        PlaybackLoadError Error)
    {
        public static BlurayStaticProbeResult UseStaticStream()
        {
            return new BlurayStaticProbeResult(false, PlaybackLoadError.None);
        }

        public static BlurayStaticProbeResult UseHls()
        {
            return new BlurayStaticProbeResult(true, PlaybackLoadError.None);
        }

        public static BlurayStaticProbeResult Failure(PlaybackLoadError error)
        {
            return new BlurayStaticProbeResult(false, error);
        }
    }

    private enum PlaybackSourceKind
    {
        DirectStream,
        Transcoding,
        StaticStream,
        BlurayHls
    }

    private sealed class PlaybackEndpointResult
    {
        private PlaybackEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            PlaybackLoadError error,
            string? apiBase)
        {
            IsSuccess = isSuccess;
            CanFallback = canFallback;
            Content = content;
            Error = error;
            ApiBase = apiBase;
        }

        public bool IsSuccess { get; }

        public bool CanFallback { get; }

        public bool ShouldTryGet => CanFallback;

        public string? Content { get; }

        public PlaybackLoadError Error { get; }

        public string? ApiBase { get; }

        public PlaybackEndpointResult WithApiBase(string apiBase)
        {
            return new PlaybackEndpointResult(IsSuccess, CanFallback, Content, Error, apiBase);
        }

        public static PlaybackEndpointResult Success(string content)
        {
            return new PlaybackEndpointResult(true, false, content, PlaybackLoadError.None, null);
        }

        public static PlaybackEndpointResult FallbackAllowed(PlaybackLoadError error)
        {
            return new PlaybackEndpointResult(false, true, null, error, null);
        }

        public static PlaybackEndpointResult NoFallback(PlaybackLoadError error)
        {
            return new PlaybackEndpointResult(false, false, null, error, null);
        }
    }
}
