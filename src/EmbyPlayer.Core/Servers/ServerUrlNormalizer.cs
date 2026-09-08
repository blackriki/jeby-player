using System.Text.RegularExpressions;

namespace EmbyPlayer.Core.Servers;

public static partial class ServerUrlNormalizer
{
    public static ServerUrlNormalizationResult Normalize(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return ServerUrlNormalizationResult.Failure(ServerConnectionError.EmptyServerUrl);
        }

        var trimmedUrl = serverUrl.Trim();
        var hasExplicitScheme = SchemePattern().IsMatch(trimmedUrl);
        var urlToParse = hasExplicitScheme ? trimmedUrl : $"http://{trimmedUrl}";

        if (!Uri.TryCreate(urlToParse, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return ServerUrlNormalizationResult.Failure(ServerConnectionError.InvalidUrl);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ServerUrlNormalizationResult.Failure(ServerConnectionError.UnsupportedScheme);
        }

        var normalizedServerBase = uri.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.UriEscaped)
            .TrimEnd('/');

        if (string.IsNullOrWhiteSpace(normalizedServerBase))
        {
            return ServerUrlNormalizationResult.Failure(ServerConnectionError.InvalidUrl);
        }

        return ServerUrlNormalizationResult.Success(new ServerConnectionInfo(normalizedServerBase));
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*://", RegexOptions.CultureInvariant)]
    private static partial Regex SchemePattern();
}
