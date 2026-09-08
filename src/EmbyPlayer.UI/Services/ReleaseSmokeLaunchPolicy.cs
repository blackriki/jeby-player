namespace EmbyPlayer.UI.Services;

public static class ReleaseSmokeLaunchPolicy
{
    public const string Argument = "--release-smoke";

    public static bool IsReleaseSmoke(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, Argument, StringComparison.Ordinal));

    public static bool RequiresSettingsCloseGuard(IEnumerable<string> arguments) =>
        !IsReleaseSmoke(arguments);
}
