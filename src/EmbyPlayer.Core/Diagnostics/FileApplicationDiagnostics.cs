using System.Globalization;
using System.Text;

namespace EmbyPlayer.Core.Diagnostics;

public sealed class FileApplicationDiagnostics : IApplicationDiagnostics
{
    public const string CurrentLogFileName = "application.log";
    public const string PreviousLogFileName = "application.previous.log";

    private const int DefaultMaximumBytes = 1024 * 1024;
    private const int MaximumDetailCharacters = 4096;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Queue<string> pendingEntries = new();
    private readonly object syncRoot = new();
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Action<string> writeEntry;
    private readonly string logDirectory;
    private readonly int maximumBytes;
    private bool workerScheduled;
    private Task? workerTask;

    public FileApplicationDiagnostics()
        : this(GetDefaultLogDirectory(), DefaultMaximumBytes, () => DateTimeOffset.UtcNow)
    {
    }

    internal FileApplicationDiagnostics(
        string logDirectory,
        int maximumBytes,
        Func<DateTimeOffset> utcNow,
        Action<string>? writeEntry = null)
    {
        this.logDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? throw new ArgumentException("A log directory is required.", nameof(logDirectory))
            : Path.GetFullPath(logDirectory);
        this.maximumBytes = maximumBytes > 0
            ? maximumBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.writeEntry = writeEntry ?? TryWriteEntry;
    }

    public void Write(string category, string eventName, string? details = null)
    {
        var safeDetails = DiagnosticRedactor.Redact(details ?? string.Empty);
        if (safeDetails.Length > MaximumDetailCharacters)
        {
            safeDetails = safeDetails[..MaximumDetailCharacters] + " [truncated]";
        }

        var entry = $"{utcNow():O} category={NormalizeName(category)} event={NormalizeName(eventName)}";
        if (safeDetails.Length > 0)
        {
            entry += " " + safeDetails;
        }

        lock (syncRoot)
        {
            pendingEntries.Enqueue(entry);
            if (workerScheduled)
            {
                return;
            }

            workerScheduled = true;
            workerTask = Task.Run(DrainQueue);
        }
    }

    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        try
        {
            await DrainPendingEntriesAsync().WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task DrainPendingEntriesAsync()
    {
        while (true)
        {
            Task? currentWorker;
            lock (syncRoot)
            {
                if (!workerScheduled)
                {
                    return;
                }

                currentWorker = workerTask;
            }

            if (currentWorker is not null)
            {
                await currentWorker.ConfigureAwait(false);
            }
        }
    }

    private void DrainQueue()
    {
        while (true)
        {
            string entry;
            lock (syncRoot)
            {
                if (pendingEntries.Count == 0)
                {
                    workerScheduled = false;
                    workerTask = null;
                    return;
                }

                entry = pendingEntries.Dequeue();
            }

            writeEntry(entry);
        }
    }

    private void TryWriteEntry(string entry)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            var currentPath = Path.Combine(logDirectory, CurrentLogFileName);
            var previousPath = Path.Combine(logDirectory, PreviousLogFileName);
            var bytes = Utf8WithoutBom.GetBytes(entry + Environment.NewLine);
            if (File.Exists(currentPath)
                && new FileInfo(currentPath).Length > 0
                && new FileInfo(currentPath).Length + bytes.Length > maximumBytes)
            {
                File.Move(currentPath, previousPath, overwrite: true);
            }

            using var stream = new FileStream(
                currentPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            stream.Write(bytes);
        }
        catch
        {
            // Application diagnostics are best effort and must never affect the caller.
        }
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var normalized = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_')
            .ToArray());
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private static string GetDefaultLogDirectory()
    {
        var testDataRoot = Environment.GetEnvironmentVariable("EMBYPLAYER_TEST_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(testDataRoot))
        {
            return Path.Combine(testDataRoot, "logs");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbyPlayer",
            "logs");
    }
}
