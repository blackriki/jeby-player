using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EmbyPlayer.Player;

public static class PlayerDebugDiagnostics
{
    private static readonly HashSet<string> AllowedStopOrigins = new(StringComparer.Ordinal)
    {
        "user",
        "navigation",
        "replace-playback",
        "load-failure",
        "app-closing",
        "dispose",
        "unknown"
    };
    private static readonly HashSet<string> SensitiveParameterNames = new(
        new[] { "api_key", "token", "access_token", "accessToken" },
        StringComparer.OrdinalIgnoreCase);

    [Conditional("DEBUG")]
    public static void WriteStopRequested(long playbackInstanceId, string origin)
    {
        WriteStopRequested(new FilePlayerDiagnostics(), playbackInstanceId, origin);
    }

    [Conditional("DEBUG")]
    internal static void WriteStopRequested(
        IPlayerDiagnostics diagnostics,
        long playbackInstanceId,
        string origin)
    {
        if (playbackInstanceId == 0)
        {
            return;
        }

        diagnostics.Write(CreateStopRequestedMessage(playbackInstanceId, origin));
    }

    internal static string CreateStopRequestedMessage(long playbackInstanceId, string origin)
    {
        var safeOrigin = AllowedStopOrigins.Contains(origin) ? origin : "unknown";
        return $"PlaybackInstance={playbackInstanceId.ToString(CultureInfo.InvariantCulture)} "
            + $"stop-requested origin={safeOrigin}";
    }

    internal static string CreatePlaybackAttemptMessage(PlayerLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var playbackInfo = request.PlaybackInfo;
        var pathInfo = ParsePlaybackPath(
            playbackInfo.PlaybackPath,
            playbackInfo.ItemId,
            playbackInfo.MediaSource.Id);
        return $"PlaybackInstance={request.PlaybackInstanceId.ToString(CultureInfo.InvariantCulture)} "
            + "playback-attempt "
            + $"itemHash8={CreateHash8(playbackInfo.ItemId)} "
            + $"mediaSourceHash8={CreateHash8(playbackInfo.MediaSource.Id)} "
            + $"container={FormatValue(playbackInfo.MediaSource.Container)} "
            + $"selectedSourceKind={GetSelectedSourceKind(playbackInfo.RequiresTranscoding, pathInfo)} "
            + $"apiPrefix={pathInfo.ApiPrefix} "
            + $"pathTemplate={pathInfo.PathTemplate} "
            + $"queryParameterNames={FormatNames(pathInfo.QueryParameterNames)} "
            + $"headerNames={FormatNames(playbackInfo.MediaSource.RequiredHttpHeaders.Keys)}";
    }

    internal static string CreateHash8(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    internal static PlaybackPathDiagnostic ParsePlaybackPath(
        string? playbackPath,
        string? itemId,
        string? mediaSourceId)
    {
        if (string.IsNullOrWhiteSpace(playbackPath))
        {
            return new PlaybackPathDiagnostic("unknown", "missing", Array.Empty<string>());
        }

        string path;
        string query;
        if (Uri.TryCreate(playbackPath, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
            query = absoluteUri.Query;
        }
        else
        {
            var queryIndex = playbackPath.IndexOf('?');
            path = queryIndex >= 0 ? playbackPath[..queryIndex] : playbackPath;
            query = queryIndex >= 0 ? playbackPath[queryIndex..] : string.Empty;
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var pathTemplate = ReplaceIdentifier(path, itemId, "{ItemId}");
        pathTemplate = ReplaceIdentifier(pathTemplate, mediaSourceId, "{MediaSourceId}");
        var apiPrefix = pathTemplate.StartsWith("/emby/", StringComparison.OrdinalIgnoreCase)
            ? "emby"
            : pathTemplate.StartsWith('/')
                ? "root"
                : "unknown";

        return new PlaybackPathDiagnostic(
            apiPrefix,
            pathTemplate,
            ParseQueryParameterNames(query));
    }

    private static string GetSelectedSourceKind(
        bool requiresTranscoding,
        PlaybackPathDiagnostic pathInfo)
    {
        if (requiresTranscoding)
        {
            return "TranscodingUrl";
        }

        var finalSegment = pathInfo.PathTemplate
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        return string.Equals(finalSegment, "stream", StringComparison.OrdinalIgnoreCase)
            && pathInfo.QueryParameterNames.Contains("static", StringComparer.OrdinalIgnoreCase)
                ? "StaticStream"
                : "DirectStreamUrl";
    }

    private static string ReplaceIdentifier(string path, string? identifier, string placeholder)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return path;
        }

        var escapedIdentifier = Uri.EscapeDataString(identifier);
        return path
            .Replace(identifier, placeholder, StringComparison.OrdinalIgnoreCase)
            .Replace(escapedIdentifier, placeholder, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseQueryParameterNames(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        return query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Select(Uri.UnescapeDataString)
            .Where(IsSafeParameterName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSafeParameterName(string name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && !SensitiveParameterNames.Contains(name)
            && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');
    }

    private static string FormatNames(IEnumerable<string> names)
    {
        var values = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? "none" : string.Join(',', values);
    }

    private static string FormatValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    internal sealed record PlaybackPathDiagnostic(
        string ApiPrefix,
        string PathTemplate,
        IReadOnlyList<string> QueryParameterNames);
}
