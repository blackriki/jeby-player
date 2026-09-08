using EmbyPlayer.Core.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class FileApplicationDiagnosticsTests
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
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
    public async Task Write_PersistsStructuredEventWithSensitiveDataAlreadyRedacted()
    {
        var root = CreateTestDirectory();
        var diagnostics = new FileApplicationDiagnostics(
            root,
            maximumBytes: 4096,
            () => new DateTimeOffset(2026, 9, 3, 5, 6, 7, TimeSpan.Zero));
        var details = "Password=" + "correct horse battery staple"
            + " url=https://media.local/item?token=" + "query-secret&mode=direct"
            + " path=C:/Users/local-user/media.mkv";

        diagnostics.Write("authentication", "failed", details);
        Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));

        var log = await File.ReadAllTextAsync(Path.Combine(root, FileApplicationDiagnostics.CurrentLogFileName));
        StringAssert.Contains(log, "2026-09-03T05:06:07.0000000+00:00");
        StringAssert.Contains(log, "category=authentication event=failed");
        StringAssert.Contains(log, "Password=[REDACTED]");
        Assert.IsFalse(log.Contains("correct horse", StringComparison.Ordinal));
        Assert.IsFalse(log.Contains("query-secret", StringComparison.Ordinal));
        Assert.IsFalse(log.Contains("local-user", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Write_WhenMaximumSizeWouldBeExceeded_RotatesOnePreviousLog()
    {
        var root = CreateTestDirectory();
        var diagnostics = new FileApplicationDiagnostics(
            root,
            maximumBytes: 180,
            () => DateTimeOffset.UnixEpoch);

        diagnostics.Write("test", "first", new string('a', 90));
        Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));
        diagnostics.Write("test", "second", new string('b', 90));
        Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));
        diagnostics.Write("test", "third", new string('c', 90));
        Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));

        var current = await File.ReadAllTextAsync(Path.Combine(root, FileApplicationDiagnostics.CurrentLogFileName));
        var previous = await File.ReadAllTextAsync(Path.Combine(root, FileApplicationDiagnostics.PreviousLogFileName));
        StringAssert.Contains(current, "event=third");
        StringAssert.Contains(previous, "event=second");
        Assert.IsFalse(previous.Contains("event=first", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Write_WhenDirectoryCannotBeCreated_DoesNotThrowOrLeaveWorkerStuck()
    {
        var root = CreateTestDirectory();
        var blockingFile = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "keep");
        var diagnostics = new FileApplicationDiagnostics(
            blockingFile,
            maximumBytes: 4096,
            () => DateTimeOffset.UnixEpoch);

        diagnostics.Write("application", "startup");
        Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));
        diagnostics.Write("application", "retry");
        Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));

        Assert.AreEqual("keep", await File.ReadAllTextAsync(blockingFile));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DefaultPath_WhenLifecycleTestRootIsSet_IsolatesTheLog()
    {
        var root = CreateTestDirectory();
        var previous = Environment.GetEnvironmentVariable("EMBYPLAYER_TEST_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("EMBYPLAYER_TEST_DATA_ROOT", root);
            var diagnostics = new FileApplicationDiagnostics();

            diagnostics.Write("application", "startup");
            Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));

            Assert.IsTrue(File.Exists(Path.Combine(
                root,
                "logs",
                FileApplicationDiagnostics.CurrentLogFileName)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EMBYPLAYER_TEST_DATA_ROOT", previous);
        }
    }

    [TestMethod]
    public async Task FlushAsync_WhenWriterIsBlocked_ReturnsAtTimeoutAndCanDrainLater()
    {
        var root = CreateTestDirectory();
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var diagnostics = new FileApplicationDiagnostics(
            root,
            maximumBytes: 4096,
            () => DateTimeOffset.UnixEpoch,
            _ =>
            {
                writerEntered.Set();
                releaseWriter.Wait();
            });

        diagnostics.Write("application", "exit");
        Assert.IsTrue(writerEntered.Wait(FlushTimeout));
        try
        {
            Assert.IsFalse(await diagnostics.FlushAsync(TimeSpan.FromMilliseconds(25)));
        }
        finally
        {
            releaseWriter.Set();
        }

        Assert.IsTrue(await diagnostics.FlushAsync(FlushTimeout));
    }

    private string CreateTestDirectory()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"EmbyPlayer.ApplicationDiagnostics.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        return testDirectory;
    }
}
