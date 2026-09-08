using System.Reflection;

namespace EmbyPlayer.Core;

public static class ApplicationIdentity
{
    public const string Name = "Jeby Player";

    public static string FullVersion { get; } =
        typeof(ApplicationIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ApplicationIdentity).Assembly.GetName().Version?.ToString()
        ?? "0.0.0-local";

    private static readonly (string Version, string Build) VersionParts = ParseVersion(FullVersion);

    public static string Version => VersionParts.Version;

    public static string Build => VersionParts.Build;

    internal static (string Version, string Build) ParseVersion(string fullVersion)
    {
        var separator = fullVersion.IndexOf('+');
        return separator < 0
            ? (fullVersion, "本地构建")
            : (fullVersion[..separator], separator < fullVersion.Length - 1 ? fullVersion[(separator + 1)..] : "本地构建");
    }
}
