using System.Net;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Emby;

internal interface IEmbyApiClient
{
    Task<(BifPreviewArchive? Archive, PlaybackPreviewError Error)> GetPlaybackPreviewArchiveAsync(
        AuthSession session, string itemId, CancellationToken cancellationToken);
}

// Central transport for the new playback preview API. Existing feature services retain their current transport.
internal sealed class EmbyApiClient(HttpClient httpClient, IDeviceIdService deviceIdService) : IEmbyApiClient
{
    private const int MaximumArchiveBytes = 32 * 1024 * 1024;

    public async Task<(BifPreviewArchive? Archive, PlaybackPreviewError Error)> GetPlaybackPreviewArchiveAsync(
        AuthSession session, string itemId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var deviceId = await deviceIdService.GetOrCreateDeviceIdAsync(timeout.Token).ConfigureAwait(false);
            var serverBase = session.ServerBase.TrimEnd('/');
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // Official authenticated BifService endpoint; never generate images or seek MPV.
                var apiBase = attempt == 0 ? serverBase : serverBase + "/emby";
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{apiBase}/Videos/{Uri.EscapeDataString(itemId)}/index.bif?Width=320");
                request.Headers.TryAddWithoutValidation("X-Emby-Token", session.AccessToken);
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization",
                    $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return (null, PlaybackPreviewError.Unauthorized);
                if (response.StatusCode == HttpStatusCode.Forbidden) return (null, PlaybackPreviewError.Forbidden);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                {
                    if (attempt == 0 && !serverBase.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) continue;
                    return (null, PlaybackPreviewError.Unavailable);
                }
                if (!response.IsSuccessStatusCode) return (null, PlaybackPreviewError.ServerError);
                if (response.Content.Headers.ContentLength > MaximumArchiveBytes) return (null, PlaybackPreviewError.InvalidResponse);
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaximumArchiveBytes) return (null, PlaybackPreviewError.InvalidResponse);
                    buffer.Write(chunk, 0, read);
                }
                timeout.Token.ThrowIfCancellationRequested();
                var archive = BifPreviewArchive.Parse(buffer.ToArray());
                if (archive is { IsEmpty: true }) return (null, PlaybackPreviewError.Unavailable);
                return (archive, archive is null ? PlaybackPreviewError.InvalidResponse : PlaybackPreviewError.None);
            }
            return (null, PlaybackPreviewError.Unavailable);
        }
        catch (OperationCanceledException) { return (null, cancellationToken.IsCancellationRequested ? PlaybackPreviewError.Cancelled : PlaybackPreviewError.ServerTimeout); }
        catch (HttpRequestException) { return (null, PlaybackPreviewError.ServerUnreachable); }
        catch (IOException) { return (null, PlaybackPreviewError.ServerUnreachable); }
    }
}
