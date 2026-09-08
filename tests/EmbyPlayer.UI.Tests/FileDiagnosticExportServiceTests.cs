using System.IO.Compression;
using System.Text;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.UI.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class FileDiagnosticExportServiceTests
{
    private string? testDirectory;

    [TestCleanup]
    public void Cleanup()
    {
        if (testDirectory is not null && Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExportAsync_IncludesOnlyWhitelistedLogsAndManifest()
    {
        var root = CreateTestDirectory();
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "application.log"), "application-safe");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "application.previous.log"), "previous-safe");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "mpv.log"), "mpv-safe");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "playback.log"), "playback-safe");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "settings.json"), "settings-secret");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "other.log"), "other-secret");
        var destination = Path.Combine(root, "diagnostics.zip");
        var exportedAt = new DateTimeOffset(2026, 9, 3, 4, 5, 6, TimeSpan.Zero);
        var diagnostics = new TestApplicationDiagnostics();
        var service = new FileDiagnosticExportService(
            logDirectory,
            "1.2.3",
            () => exportedAt,
            diagnostics: diagnostics);

        var result = await service.ExportAsync(destination, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "application.log",
                "application.previous.log",
                "mpv.log",
                "playback.log"
            },
            result.IncludedLogNames.ToArray());
        using var archive = ZipFile.OpenRead(destination);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "logs/application.log",
                "logs/application.previous.log",
                "logs/mpv.log",
                "logs/playback.log",
                "manifest.txt"
            },
            archive.Entries.Select(entry => entry.FullName).ToArray());

        var manifest = ReadEntry(archive, "manifest.txt");
        StringAssert.Contains(manifest, "App version: 1.2.3");
        StringAssert.Contains(manifest, "Exported UTC: 2026-09-03T04:05:06.0000000Z");
        StringAssert.Contains(manifest, "- logs/mpv.log");
        StringAssert.Contains(manifest, "replaced with [REDACTED]");
        Assert.IsFalse(manifest.Contains(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.Contains("settings", StringComparison.Ordinal)));
        Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.Contains("other.log", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Events.Any(entry =>
            entry.Category == "diagnostic-export"
            && entry.EventName == "export-succeeded"
            && entry.Details == "includedLogCount=4"));
    }

    [TestMethod]
    public async Task ExportAsync_RedactsSensitiveAssignmentsHeadersAndUrls()
    {
        var root = CreateTestDirectory();
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDirectory);
        var logText = string.Join(
            "\r\n",
            "safe-line",
            "GET https://media.local/stream?api_key=" + "query-secret&mode=direct",
            "GET https://media.local/stream?access%5Ftoken=encoded-secret",
            "GET https://[::1]:8096/path?mode=private",
            "X-Emby-Token: header-secret",
            "X-Emby-Authorization: MediaBrowser Client=Player, Token=auth-secret",
            "Authorization=Bearer bearer-secret",
            "PaSsWoRd=" + "plain secret with spaces",
            "Pw='quoted-secret'",
            "{\"Password\":\"escaped \\\"json-secret\\\" value\",\"Name\":\"keep\"}",
            "https://user-secret:password-secret@media.local/video",
            "local path C:\\Users\\local-user\\Videos\\movie.mkv",
            "local path C:/Users/forward-user/Videos/movie.mkv",
            "local path \\\\server\\share\\unc-user\\movie.mkv",
            "file://server/share/file-user/movie.mkv",
            "endpoint https://media.local/System/Info/Public",
            "token validation failed but no value",
            "normal status=500 retry=true");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "playback.log"), logText);
        var destination = Path.Combine(root, "diagnostics.zip");
        var service = new FileDiagnosticExportService(
            logDirectory,
            "1.0.0",
            () => DateTimeOffset.UnixEpoch,
            diagnostics: new TestApplicationDiagnostics());

        await service.ExportAsync(destination, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var exportedLog = ReadEntry(archive, "logs/playback.log");
        StringAssert.Contains(exportedLog, "safe-line");
        StringAssert.Contains(
            exportedLog,
            "https://media.local/stream?api_" + "key=[REDACTED]&mode=[REDACTED]");
        StringAssert.Contains(exportedLog, "access%5Ftoken=[REDACTED]");
        StringAssert.Contains(exportedLog, "https://[::1]:8096/path?mode=[REDACTED]");
        Assert.IsFalse(exportedLog.Contains("mode=private", StringComparison.Ordinal));
        StringAssert.Contains(exportedLog, "[REDACTED]");
        StringAssert.Contains(exportedLog, "endpoint https://media.local/System/Info/Public");
        StringAssert.Contains(exportedLog, "token validation failed but no value");
        StringAssert.Contains(exportedLog, "normal status=500 retry=true");
        foreach (var secret in new[]
                 {
                     "query-secret",
                     "encoded-secret",
                     "header-secret",
                     "auth-secret",
                     "bearer-secret",
                     "plain secret with spaces",
                     "quoted-secret",
                     "json-secret",
                     "user-secret",
                     "password-secret",
                     "local-user",
                     "forward-user",
                     "unc-user",
                     "file-user"
                 })
        {
            Assert.IsFalse(exportedLog.Contains(secret, StringComparison.Ordinal), secret);
        }
    }

    [DataTestMethod]
    [DataRow("api_key")]
    [DataRow("password")]
    public async Task ExportAsync_WhenTailStartsAtSensitiveValue_DiscardsIncompleteLine(
        string sensitiveName)
    {
        var root = CreateTestDirectory();
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDirectory);
        var secret = sensitiveName + "-boundary-secret";
        var completeLine = "complete status=ok";
        var content = "old-prefix " + sensitiveName + "=" + secret + "\n" + completeLine;
        var maximumBytes = Encoding.UTF8.GetByteCount(secret + "\n" + completeLine);
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "playback.log"), content);
        var destination = Path.Combine(root, "diagnostics.zip");
        var service = new FileDiagnosticExportService(
            logDirectory,
            "1.0.0",
            () => DateTimeOffset.UnixEpoch,
            maximumLogBytes: maximumBytes,
            diagnostics: new TestApplicationDiagnostics());

        await service.ExportAsync(destination, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        Assert.AreEqual(completeLine, ReadEntry(archive, "logs/playback.log"));
        Assert.IsFalse(ReadEntry(archive, "logs/playback.log").Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(
            ReadEntry(archive, "manifest.txt"),
            "older content and the leading partial line were omitted");
    }

    [TestMethod]
    public async Task ExportAsync_WhenTailStartsInsideUtf8Character_DecodesAtNextLineBoundary()
    {
        var root = CreateTestDirectory();
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDirectory);
        const string completeLine = "保留中文日志";
        var content = "旧旧旧\n" + completeLine;
        var contentBytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(Path.Combine(logDirectory, "mpv.log"), contentBytes);
        var destination = Path.Combine(root, "diagnostics.zip");
        var service = new FileDiagnosticExportService(
            logDirectory,
            "1.0.0",
            () => DateTimeOffset.UnixEpoch,
            maximumLogBytes: contentBytes.Length - 1,
            diagnostics: new TestApplicationDiagnostics());

        await service.ExportAsync(destination, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        var exportedLog = ReadEntry(archive, "logs/mpv.log");
        Assert.AreEqual(completeLine, exportedLog);
        Assert.IsFalse(exportedLog.Contains('\uFFFD'));
    }

    [TestMethod]
    public async Task ExportAsync_WhenOversizedLogHasNoLineBoundary_OmitsContentAndMarksManifest()
    {
        var root = CreateTestDirectory();
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDirectory);
        var secret = "single-line-" + "password-secret";
        await File.WriteAllTextAsync(
            Path.Combine(logDirectory, "playback.log"),
            new string('x', 128) + " password=" + secret);
        var destination = Path.Combine(root, "diagnostics.zip");
        var service = new FileDiagnosticExportService(
            logDirectory,
            "1.0.0",
            () => DateTimeOffset.UnixEpoch,
            maximumLogBytes: 32,
            diagnostics: new TestApplicationDiagnostics());

        await service.ExportAsync(destination, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);
        Assert.AreEqual(string.Empty, ReadEntry(archive, "logs/playback.log"));
        Assert.IsFalse(ReadEntry(archive, "logs/playback.log").Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(
            ReadEntry(archive, "manifest.txt"),
            "content omitted: no complete line boundary within the size limit");
    }

    [TestMethod]
    public async Task ExportAsync_WhenNoLogsExist_CreatesManifestOnlyPackage()
    {
        var root = CreateTestDirectory();
        var logDirectory = Path.Combine(root, "missing-logs");
        var destination = Path.Combine(root, "diagnostics.zip");
        var diagnostics = new TestApplicationDiagnostics();
        var service = new FileDiagnosticExportService(
            logDirectory,
            "1.0.0",
            () => DateTimeOffset.UnixEpoch,
            diagnostics: diagnostics);

        var result = await service.ExportAsync(destination, CancellationToken.None);

        Assert.AreEqual(0, result.IncludedLogCount);
        using var archive = ZipFile.OpenRead(destination);
        Assert.AreEqual(1, archive.Entries.Count);
        Assert.AreEqual("manifest.txt", archive.Entries[0].FullName);
        StringAssert.Contains(ReadEntry(archive, "manifest.txt"), "no diagnostic log files were available");
    }

    [TestMethod]
    public async Task ExportAsync_WhenDestinationIsLocked_ThrowsAndRemovesTemporaryFile()
    {
        var root = CreateTestDirectory();
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "mpv.log"), "safe");
        var destination = Path.Combine(root, "locked.zip");
        await File.WriteAllTextAsync(destination, "original");
        var diagnostics = new TestApplicationDiagnostics();
        var service = new FileDiagnosticExportService(
            logDirectory,
            "1.0.0",
            () => DateTimeOffset.UnixEpoch,
            diagnostics: diagnostics);

        await using (var lockedFile = new FileStream(
                         destination,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
                () => service.ExportAsync(destination, CancellationToken.None));
        }

        Assert.AreEqual("original", await File.ReadAllTextAsync(destination));
        Assert.AreEqual(0, Directory.GetFiles(root, ".locked.zip.*.tmp").Length);
        var exportEvent = diagnostics.Events.Single();
        Assert.AreEqual("diagnostic-export", exportEvent.Category);
        Assert.AreEqual("export-failed", exportEvent.EventName);
        StringAssert.Contains(exportEvent.Details, "errorType=UnauthorizedAccessException");
        Assert.IsFalse(exportEvent.Details!.Contains(destination, StringComparison.OrdinalIgnoreCase));
    }

    private string CreateTestDirectory()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"EmbyPlayer.DiagnosticExport.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        return testDirectory;
    }

    private static string ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new AssertFailedException($"ZIP entry was not found: {entryName}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class TestApplicationDiagnostics : IApplicationDiagnostics
    {
        public List<DiagnosticEvent> Events { get; } = new();

        public void Write(string category, string eventName, string? details = null)
        {
            Events.Add(new DiagnosticEvent(category, eventName, details));
        }

        public Task<bool> FlushAsync(TimeSpan timeout) => Task.FromResult(true);

        public sealed record DiagnosticEvent(string Category, string EventName, string? Details);
    }
}
