using System.Net;
using System.Text;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Emby;

public sealed class EmbyPlaybackReportService : IPlaybackReportService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IApplicationDiagnostics diagnostics;
    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;

    public EmbyPlaybackReportService(
        HttpClient httpClient,
        IDeviceIdService deviceIdService,
        IApplicationDiagnostics? diagnostics = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;
    }

    public Task<PlaybackReportResult> ReportPlayingAsync(
        AuthSession session,
        PlaybackReportStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return SendReportWithFallbackAsync(
            session,
            "/Sessions/Playing",
            CreateBody(
                request.ItemId,
                request.MediaSourceId,
                request.PlaySessionId,
                request.PositionTicks,
                request.RunTimeTicks,
                request.IsPaused,
                request.CanSeek,
                request.PlayMethod,
                request.IsMuted,
                request.VolumeLevel,
                request.AudioStreamIndex,
                request.SubtitleStreamIndex),
            cancellationToken);
    }

    public Task<PlaybackReportResult> ReportProgressAsync(
        AuthSession session,
        PlaybackReportProgressRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return SendReportWithFallbackAsync(
            session,
            "/Sessions/Playing/Progress",
            CreateBody(
                request.ItemId,
                request.MediaSourceId,
                request.PlaySessionId,
                request.PositionTicks,
                request.RunTimeTicks,
                request.IsPaused,
                request.CanSeek,
                request.PlayMethod,
                request.IsMuted,
                request.VolumeLevel,
                request.AudioStreamIndex,
                request.SubtitleStreamIndex),
            cancellationToken);
    }

    public Task<PlaybackReportResult> ReportStoppedAsync(
        AuthSession session,
        PlaybackReportStoppedRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return SendReportWithFallbackAsync(
            session,
            "/Sessions/Playing/Stopped",
            CreateBody(
                request.ItemId,
                request.MediaSourceId,
                request.PlaySessionId,
                request.PositionTicks,
                request.RunTimeTicks,
                request.IsPaused,
                request.CanSeek,
                request.PlayMethod,
                request.IsMuted,
                request.VolumeLevel,
                request.AudioStreamIndex,
                request.SubtitleStreamIndex),
            cancellationToken);
    }

    private async Task<PlaybackReportResult> SendReportWithFallbackAsync(
        AuthSession session,
        string endpointPath,
        PlaybackReportBody body,
        CancellationToken cancellationToken)
    {
        var serverBase = session.ServerBase.TrimEnd('/');
        var primaryResult = await SendReportAsync(
                serverBase,
                endpointPath,
                session.AccessToken,
                body,
                cancellationToken)
            .ConfigureAwait(false);

        if (!CanFallback(primaryResult.Error))
        {
            WriteReportFailure(endpointPath, primaryResult.Error);
            return primaryResult;
        }

        var fallbackResult = await SendReportAsync(
                $"{serverBase}/emby",
                endpointPath,
                session.AccessToken,
                body,
                cancellationToken)
            .ConfigureAwait(false);
        WriteReportFailure(endpointPath, fallbackResult.Error);
        return fallbackResult;
    }

    private async Task<PlaybackReportResult> SendReportAsync(
        string apiBase,
        string endpointPath,
        string accessToken,
        PlaybackReportBody body,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            var deviceId = await deviceIdService
                .GetOrCreateDeviceIdAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri($"{apiBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute));
            request.Headers.TryAddWithoutValidation("X-Emby-Token", accessToken);
            request.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return PlaybackReportResult.Failure(PlaybackReportError.Unauthorized);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return PlaybackReportResult.Failure(PlaybackReportError.Forbidden);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return PlaybackReportResult.Failure(PlaybackReportError.PathMismatch);
            }

            return response.IsSuccessStatusCode
                ? PlaybackReportResult.Success()
                : PlaybackReportResult.Failure(PlaybackReportError.ServerError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlaybackReportResult.Failure(PlaybackReportError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return PlaybackReportResult.Failure(PlaybackReportError.ServerTimeout);
        }
        catch (HttpRequestException)
        {
            return PlaybackReportResult.Failure(PlaybackReportError.ServerUnreachable);
        }
        catch (IOException)
        {
            return PlaybackReportResult.Failure(PlaybackReportError.ServerError);
        }
        catch (UnauthorizedAccessException)
        {
            return PlaybackReportResult.Failure(PlaybackReportError.ServerError);
        }
    }

    private static bool CanFallback(PlaybackReportError error)
    {
        return error == PlaybackReportError.PathMismatch;
    }

    private void WriteReportFailure(string endpointPath, PlaybackReportError error)
    {
        if (error is PlaybackReportError.None or PlaybackReportError.Cancelled)
        {
            return;
        }

        var reportKind = endpointPath.EndsWith("/Progress", StringComparison.Ordinal)
            ? "progress"
            : endpointPath.EndsWith("/Stopped", StringComparison.Ordinal)
                ? "stopped"
                : "started";
        diagnostics.Write(
            "playback-report",
            "failed",
            $"kind={reportKind} reason={error}");
    }

    private static PlaybackReportBody CreateBody(
        string itemId,
        string? mediaSourceId,
        string? playSessionId,
        long positionTicks,
        long? runTimeTicks,
        bool isPaused,
        bool canSeek,
        string playMethod,
        bool isMuted,
        int? volumeLevel,
        int? audioStreamIndex,
        int? subtitleStreamIndex)
    {
        return new PlaybackReportBody(
            itemId,
            mediaSourceId,
            playSessionId,
            Math.Max(0, positionTicks),
            runTimeTicks,
            isPaused,
            canSeek,
            playMethod,
            isMuted,
            volumeLevel.HasValue ? Math.Clamp(volumeLevel.Value, 0, 100) : null,
            audioStreamIndex,
            subtitleStreamIndex);
    }

    private sealed record PlaybackReportBody(
        string ItemId,
        string? MediaSourceId,
        string? PlaySessionId,
        long PositionTicks,
        long? RunTimeTicks,
        bool IsPaused,
        bool CanSeek,
        string PlayMethod,
        bool IsMuted,
        int? VolumeLevel,
        int? AudioStreamIndex,
        int? SubtitleStreamIndex);
}
