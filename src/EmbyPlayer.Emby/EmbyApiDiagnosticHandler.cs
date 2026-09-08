using EmbyPlayer.Core.Diagnostics;

namespace EmbyPlayer.Emby;

public sealed class EmbyApiDiagnosticHandler : DelegatingHandler
{
    private readonly IApplicationDiagnostics diagnostics;

    public EmbyApiDiagnosticHandler(
        HttpMessageHandler innerHandler,
        IApplicationDiagnostics? diagnostics = null)
        : base(innerHandler ?? throw new ArgumentNullException(nameof(innerHandler)))
    {
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            diagnostics.Write(
                "api",
                "non-success",
                $"method={NormalizeMethod(request.Method)} status={(int)response.StatusCode} requestCategory={ClassifyRequest(request.RequestUri)}");
        }
        catch
        {
            // Diagnostics must never alter the HTTP result seen by the application.
        }

        return response;
    }

    private static string NormalizeMethod(HttpMethod method)
    {
        var value = method.Method;
        return value.Length is > 0 and <= 16 && value.All(char.IsAsciiLetter)
            ? value.ToUpperInvariant()
            : "OTHER";
    }

    private static string ClassifyRequest(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return "unknown";
        }

        var path = requestUri.AbsolutePath;
        if (path.Contains("/System/Info/Public", StringComparison.OrdinalIgnoreCase))
        {
            return "server-probe";
        }

        if (path.Contains("/AuthenticateByName", StringComparison.OrdinalIgnoreCase))
        {
            return "authentication";
        }

        if (path.Contains("/Sessions/Playing", StringComparison.OrdinalIgnoreCase))
        {
            return "playback-report";
        }

        if (path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            return "playback";
        }

        if (path.Contains("/UserData", StringComparison.OrdinalIgnoreCase))
        {
            return "user-data";
        }

        if (path.Contains("/Images/", StringComparison.OrdinalIgnoreCase))
        {
            return "image";
        }

        if (path.Contains("/Episodes", StringComparison.OrdinalIgnoreCase))
        {
            return "episodes";
        }

        if (path.Contains("/Seasons", StringComparison.OrdinalIgnoreCase))
        {
            return "seasons";
        }

        if (path.Contains("/Library/Refresh", StringComparison.OrdinalIgnoreCase))
        {
            return "library-scan";
        }

        if (path.Contains("/Items", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Shows/", StringComparison.OrdinalIgnoreCase))
        {
            return "items";
        }

        if (path.Contains("/Videos/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase))
        {
            return "media";
        }

        return "emby-api";
    }
}
