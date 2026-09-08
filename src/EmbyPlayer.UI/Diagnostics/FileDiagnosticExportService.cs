using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using EmbyPlayer.Core.Diagnostics;

namespace EmbyPlayer.UI.Diagnostics;

internal sealed class FileDiagnosticExportService : IDiagnosticExportService
{
    private const int MaxExportedLogBytes = 8 * 1024 * 1024;
    private static readonly string[] AllowedLogFileNames =
    {
        FileApplicationDiagnostics.CurrentLogFileName,
        FileApplicationDiagnostics.PreviousLogFileName,
        "mpv.log",
        "playback.log"
    };

    private readonly string appVersion;
    private readonly IApplicationDiagnostics diagnostics;
    private readonly string logDirectory;
    private readonly int maximumLogBytes;
    private readonly Func<DateTimeOffset> utcNow;

    public FileDiagnosticExportService()
        : this(GetDefaultLogDirectory(), GetApplicationVersion(), () => DateTimeOffset.UtcNow)
    {
    }

    internal FileDiagnosticExportService(
        string logDirectory,
        string appVersion,
        Func<DateTimeOffset> utcNow,
        int maximumLogBytes = MaxExportedLogBytes,
        IApplicationDiagnostics? diagnostics = null)
    {
        this.logDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? throw new ArgumentException("A log directory is required.", nameof(logDirectory))
            : Path.GetFullPath(logDirectory);
        this.appVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion.Trim();
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.maximumLogBytes = maximumLogBytes > 0
            ? maximumLogBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumLogBytes));
        this.diagnostics = diagnostics ?? ApplicationDiagnosticLog.Shared;
    }

    public Task<DiagnosticExportResult> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(fullDestinationPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The destination must be a ZIP file.", nameof(destinationPath));
        }

        return Task.Run(
            () => ExportCore(fullDestinationPath, cancellationToken),
            cancellationToken);
    }

    internal static string RedactSensitiveText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return DiagnosticRedactor.Redact(value);
    }

    private DiagnosticExportResult ExportCore(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("The destination must include a directory.", nameof(destinationPath));
        }

        var includedLogNames = AllowedLogFileNames
            .Where(fileName => File.Exists(Path.Combine(logDirectory, fileName)))
            .ToArray();
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var exportedLogs = new List<ExportedLog>();
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var logName in includedLogNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var logReadResult = ReadLogText(Path.Combine(logDirectory, logName));
                    WriteTextEntry(
                        archive,
                        $"logs/{logName}",
                        RedactSensitiveText(logReadResult.Content));
                    exportedLogs.Add(new ExportedLog(
                        logName,
                        logReadResult.WasTruncated,
                        logReadResult.WasOmitted));
                }

                WriteTextEntry(
                    archive,
                    "manifest.txt",
                    CreateManifest(exportedLogs, utcNow()));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            TryWriteExportEvent("export-succeeded", includedLogNames.Length, exception: null);
            return new DiagnosticExportResult(includedLogNames);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            TryWriteExportEvent("export-failed", includedLogNames.Length, exception);
            throw;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private string CreateManifest(
        IReadOnlyList<ExportedLog> exportedLogs,
        DateTimeOffset exportedAtUtc)
    {
        var lines = new List<string>
        {
            $"{EmbyPlayer.Core.ApplicationIdentity.Name} diagnostic export",
            $"App version: {appVersion}",
            $"Exported UTC: {exportedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}",
            "Included logs:"
        };

        if (exportedLogs.Count == 0)
        {
            lines.Add("- none (no diagnostic log files were available)");
        }
        else
        {
            foreach (var exportedLog in exportedLogs)
            {
                var note = exportedLog.WasOmitted
                    ? " (content omitted: no complete line boundary within the size limit)"
                    : exportedLog.WasTruncated
                        ? " (older content and the leading partial line were omitted)"
                        : string.Empty;
                lines.Add($"- logs/{exportedLog.Name}{note}");
            }
        }

        lines.Add(string.Empty);
        lines.Add(
            "Redaction: password, authentication header, access token, API key, bearer token, "
            + "URL credential values, and all HTTP(S) query parameter values are replaced with [REDACTED].");
        lines.Add("Local Windows and file URI paths are replaced with [LOCAL_PATH_REDACTED].");
        lines.Add(
            "Excluded: settings, saved credentials, caches, media files, and machine-specific paths.");
        lines.Add("Size limit: at most the newest 8 MiB of each log is included.");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private LogReadResult ReadLogText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var capturedLength = stream.Length;
        var wasTruncated = capturedLength > maximumLogBytes;
        var bytesToRead = (int)Math.Min(capturedLength, maximumLogBytes);
        var startsAtLineBoundary = true;
        if (wasTruncated)
        {
            var startPosition = capturedLength - bytesToRead;
            stream.Seek(startPosition - 1, SeekOrigin.Begin);
            startsAtLineBoundary = stream.ReadByte() == '\n';
            stream.Seek(startPosition, SeekOrigin.Begin);
        }

        var bytes = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < bytes.Length)
        {
            var read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var contentOffset = 0;
        if (wasTruncated && !startsAtLineBoundary)
        {
            var firstLineEnd = Array.IndexOf(bytes, (byte)'\n', 0, totalRead);
            if (firstLineEnd < 0)
            {
                return new LogReadResult(string.Empty, WasTruncated: true, WasOmitted: true);
            }

            contentOffset = firstLineEnd + 1;
        }

        return new LogReadResult(
            Encoding.UTF8.GetString(bytes, contentOffset, totalRead - contentOffset),
            wasTruncated,
            WasOmitted: false);
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private void TryWriteExportEvent(
        string eventName,
        int includedLogCount,
        Exception? exception)
    {
        try
        {
            var suffix = exception is null
                ? string.Empty
                : $" errorType={exception.GetType().Name} hresult={exception.HResult.ToString(CultureInfo.InvariantCulture)}";
            diagnostics.Write(
                "diagnostic-export",
                eventName,
                $"includedLogCount={includedLogCount.ToString(CultureInfo.InvariantCulture)}{suffix}");
        }
        catch
        {
            // A best-effort diagnostic event must not replace the export result.
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch
        {
            // The original export exception remains more useful than cleanup failure.
        }
    }

    private static string GetDefaultLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbyPlayer",
            "logs");
    }

    private static string GetApplicationVersion()
    {
        return EmbyPlayer.Core.ApplicationIdentity.FullVersion;
    }

    private sealed record LogReadResult(string Content, bool WasTruncated, bool WasOmitted);

    private sealed record ExportedLog(string Name, bool WasTruncated, bool WasOmitted);
}
