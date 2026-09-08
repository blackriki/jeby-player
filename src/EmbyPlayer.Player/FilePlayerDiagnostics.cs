using System.Text;

namespace EmbyPlayer.Player;

internal sealed class FilePlayerDiagnostics : IPlayerDiagnostics
{
    public void Write(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmbyPlayer",
                "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "mpv.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never make playback fail.
        }
    }
}
