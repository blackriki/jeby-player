namespace EmbyPlayer.Core.Diagnostics;

public interface IApplicationDiagnostics
{
    void Write(string category, string eventName, string? details = null);

    Task<bool> FlushAsync(TimeSpan timeout);
}
