namespace EmbyPlayer.Core.Diagnostics;

public static class ApplicationDiagnosticLog
{
    public static IApplicationDiagnostics Shared { get; } = new FileApplicationDiagnostics();
}
