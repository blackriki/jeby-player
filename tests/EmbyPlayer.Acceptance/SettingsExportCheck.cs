using System.IO;
using System.IO.Compression;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.Diagnostics;

internal static partial class Program
{
    private static async Task RunSettingsExportAsync(string root, CheckResult result, CancellationToken cancellationToken)
    {
        result.Details["scope"] = "isolated-settings-and-synthetic-logs";
        result.Details["network"] = "NotRun";
        result.Details["realUserLogs"] = "NotRun";
        result.Details["gui"] = "NotRun";
        var settingsPath = Path.Combine(root, "settings.json");
        var original = await new FileAppSettingsService(settingsPath).GetPlayerPreferencesAsync(cancellationToken);
        var changed = original with { SeekSeconds = original.SeekSeconds == 30 ? 15 : 30 };
        await new FileAppSettingsService(settingsPath).SavePlayerPreferencesAsync(changed, cancellationToken);
        result.Assert("settings-save-reload",
            await new FileAppSettingsService(settingsPath).GetPlayerPreferencesAsync(cancellationToken) == changed);
        await new FileAppSettingsService(settingsPath).SavePlayerPreferencesAsync(original, cancellationToken);
        result.Assert("settings-restore-reload",
            await new FileAppSettingsService(settingsPath).GetPlayerPreferencesAsync(cancellationToken) == original);

        var logs = Path.Combine(root, "synthetic-logs");
        Directory.CreateDirectory(logs);
        string[] allowed = ["application.log", "application.previous.log", "mpv.log", "playback.log"];
        const string chinese = "正常中文日志：暂停、恢复、字幕验收。";
        string[] secrets = ["fixture-header", "fixture-bearer", "fixture-password", "fixture-query", "fixture-local"];
        var sample = string.Join('\n', chinese,
            "X-Emby-Token: " + secrets[0], "Authorization: Bearer " + secrets[1], "password=" + secrets[2],
            "GET https://media.example.invalid/stream?api_key=" + secrets[3],
            "local C:\\Users\\" + secrets[4] + "\\movie.mkv") + "\n";
        foreach (var name in allowed)
            await File.WriteAllTextAsync(Path.Combine(logs, name), sample, Utf8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "settings.json"), "excluded-fixture", Utf8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(logs, "other.log"), "excluded-fixture", Utf8, cancellationToken);

        var diagnostics = new ExportEvents();
        var exportedAt = DateTimeOffset.UtcNow;
        var version = result.Artifact!.Version;
        var destination = Path.Combine(root, "synthetic-diagnostics.zip");
        var export = await new FileDiagnosticExportService(logs, version, () => exportedAt, diagnostics: diagnostics)
            .ExportAsync(destination, cancellationToken);
        using (var zip = ZipFile.OpenRead(destination))
        {
            result.Assert("export-whitelist", export.IncludedLogNames.SequenceEqual(allowed)
                && zip.Entries.Select(entry => entry.FullName).Order().SequenceEqual(
                    allowed.Select(name => "logs/" + name).Append("manifest.txt").Order()));
            var texts = zip.Entries.ToDictionary(entry => entry.FullName, ReadUtf8);
            var manifest = texts["manifest.txt"];
            result.Assert("export-manifest", manifest.Contains("App version: " + version, StringComparison.Ordinal)
                && manifest.Contains("Exported UTC: " + exportedAt.UtcDateTime.ToString("O"), StringComparison.Ordinal)
                && allowed.All(name => manifest.Contains("- logs/" + name, StringComparison.Ordinal))
                && !manifest.Contains(root, StringComparison.OrdinalIgnoreCase));
            result.Assert("export-utf8", allowed.All(name => texts["logs/" + name].Contains(chinese, StringComparison.Ordinal))
                && texts.Values.All(text => !text.Contains('\uFFFD')));
            var allText = string.Join('\n', texts.Values);
            result.Assert("export-redaction", secrets.Append("excluded-fixture").All(secret => !allText.Contains(secret, StringComparison.Ordinal))
                && allowed.All(name => texts["logs/" + name].Contains("[REDACTED]", StringComparison.Ordinal)
                    && texts["logs/" + name].Contains("[LOCAL_PATH_REDACTED]", StringComparison.Ordinal)));
        }
        result.Assert("export-success-event", diagnostics.SuccessCount == 1);

        const string tail = "完整尾行\n";
        var boundaryLogs = Path.Combine(root, "boundary-logs");
        Directory.CreateDirectory(boundaryLogs);
        await File.WriteAllTextAsync(Path.Combine(boundaryLogs, "application.log"), new string('中', 20) + "\n" + tail, Utf8, cancellationToken);
        var boundaryZip = Path.Combine(root, "boundary-diagnostics.zip");
        await new FileDiagnosticExportService(boundaryLogs, version, () => exportedAt,
            maximumLogBytes: Utf8.GetByteCount(tail) + 3, diagnostics: diagnostics).ExportAsync(boundaryZip, cancellationToken);
        using (var zip = ZipFile.OpenRead(boundaryZip))
            result.Assert("export-utf8-truncation-boundary", ReadUtf8(zip.GetEntry("logs/application.log")!) == tail
                && ReadUtf8(zip.GetEntry("manifest.txt")!).Contains("older content and the leading partial line were omitted", StringComparison.Ordinal));
        result.Assert("temporary-files-cleaned", !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
        result.Details["exports"] = new[] { destination, boundaryZip }.Select(path => new { fileName = Path.GetFileName(path), sha256 = HashFile(path) }).ToArray();
    }

    private static string ReadUtf8(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Utf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private sealed class ExportEvents : IApplicationDiagnostics
    {
        public int SuccessCount { get; private set; }
        public void Write(string category, string eventName, string? details = null)
        {
            if (category == "diagnostic-export" && eventName == "export-succeeded") SuccessCount++;
        }
        public Task<bool> FlushAsync(TimeSpan timeout) => Task.FromResult(true);
    }
}
