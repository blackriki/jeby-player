using EmbyPlayer.Core.Diagnostics;

namespace EmbyPlayer.Emby.Tests;

internal sealed class TestApplicationDiagnostics : IApplicationDiagnostics
{
    public List<DiagnosticEvent> Events { get; } = new();

    public void Write(string category, string eventName, string? details = null)
    {
        Events.Add(new DiagnosticEvent(category, eventName, details));
    }

    public Task<bool> FlushAsync(TimeSpan timeout) => Task.FromResult(true);

    internal sealed record DiagnosticEvent(string Category, string EventName, string? Details);
}
