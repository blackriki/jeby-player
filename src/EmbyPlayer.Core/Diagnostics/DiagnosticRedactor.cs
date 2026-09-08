using System.Text.RegularExpressions;

namespace EmbyPlayer.Core.Diagnostics;

public static class DiagnosticRedactor
{
    private const string RedactedValue = "[REDACTED]";
    private const string LocalPathRedactedValue = "[LOCAL_PATH_REDACTED]";

    private static readonly Regex SensitiveLineRegex = new(
        @"(?im)(?<prefix>(?<![\w-])[""']?(?:x-emby-authorization|x-emby-token|authorization|password|passwd|pwd|pw|client(?:_|-)?secret|refresh(?:_|-)?token|access(?:_|-)?token|api(?:_|-)?key|token)[""']?[ \t]*[:=](?>[ \t]*))(?!\[REDACTED\]).*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex BearerTokenRegex = new(
        @"\bBearer\s+[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HttpUrlRegex = new(
        @"https?://[^\s""'<>()]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FileUriRegex = new(
        @"\bfile:(?://+)[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsAbsolutePathRegex = new(
        @"(?<![\w])(?:[a-z]:[\\/]|\\\\)[^\r\n""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return value;
        }

        var redacted = FileUriRegex.Replace(value, LocalPathRedactedValue);
        redacted = WindowsAbsolutePathRegex.Replace(redacted, LocalPathRedactedValue);
        redacted = HttpUrlRegex.Replace(redacted, RedactHttpUrl);
        redacted = SensitiveLineRegex.Replace(redacted, "${prefix}" + RedactedValue);
        return BearerTokenRegex.Replace(redacted, "Bearer " + RedactedValue);
    }

    private static string RedactHttpUrl(Match match)
    {
        var value = match.Value;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var queryIndex = value.IndexOf('?', schemeEnd);
        var authorityEnd = value.IndexOf('/', schemeEnd);
        if (authorityEnd < 0 || (queryIndex >= 0 && authorityEnd > queryIndex))
        {
            authorityEnd = queryIndex >= 0 ? queryIndex : value.Length;
        }

        var atIndex = value.IndexOf('@', schemeEnd, authorityEnd - schemeEnd);
        if (atIndex >= 0)
        {
            value = value[..schemeEnd] + RedactedValue + value[atIndex..];
            queryIndex = value.IndexOf('?', schemeEnd);
        }

        if (queryIndex < 0)
        {
            return value;
        }

        var fragmentIndex = value.IndexOf('#', queryIndex + 1);
        var queryEnd = fragmentIndex >= 0 ? fragmentIndex : value.Length;
        var query = value[(queryIndex + 1)..queryEnd];
        var redactedParameters = query
            .Split('&', StringSplitOptions.None)
            .Select(RedactQueryParameter);
        return value[..(queryIndex + 1)]
            + string.Join('&', redactedParameters)
            + (fragmentIndex >= 0 ? value[fragmentIndex..] : string.Empty);
    }

    private static string RedactQueryParameter(string parameter)
    {
        var separatorIndex = parameter.IndexOf('=');
        return separatorIndex < 0
            ? parameter
            : parameter[..(separatorIndex + 1)] + RedactedValue;
    }
}
