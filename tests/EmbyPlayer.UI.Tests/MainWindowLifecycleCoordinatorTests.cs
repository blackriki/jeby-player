using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class MainWindowLifecycleCoordinatorTests
{
    [TestMethod]
    public async Task App_UsesExplicitMainWindowShutdownAndExitDiagnostic()
    {
        var root = FindRepositoryRoot();
        var xaml = await File.ReadAllTextAsync(Path.Combine(root, "src", "EmbyPlayer.App", "App.xaml"));
        var code = await File.ReadAllTextAsync(Path.Combine(root, "src", "EmbyPlayer.App", "App.xaml.cs"));

        StringAssert.Contains(xaml, "ShutdownMode=\"OnMainWindowClose\"");
        StringAssert.Contains(code, "protected override void OnExit(ExitEventArgs e)");
        StringAssert.Contains(code, "Write(\"application\", \"exit\")");
        StringAssert.Contains(code, ".FlushAsync(DiagnosticFlushTimeout)");
    }

    [TestMethod]
    public void EvaluateClosing_WhenSettingsGuardCancels_DoesNotPrepareOrClose()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);

        var decision = coordinator.EvaluateClosing(
            isReleaseSmoke: false,
            bypassSettingsCloseGuard: false,
            requestSettingsClose: () => false,
            pageCategory: "settings");

        Assert.AreEqual(MainWindowClosingDecision.Cancel, decision);
        Assert.AreEqual(1, diagnostics.Entries.Count);
        StringAssert.Contains(diagnostics.Entries[0], "event=closing");
        StringAssert.Contains(diagnostics.Entries[0], "cancelled=true");
        Assert.IsFalse(diagnostics.Entries.Any(entry => entry.Contains("event=closed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PrepareClose_WhenCleanupFails_StillClosesAndRequestsShutdown()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);
        var closeRequests = 0;
        var shutdownRequests = 0;
        Assert.AreEqual(
            MainWindowClosingDecision.Prepare,
            coordinator.EvaluateClosing(false, true, () => true, "player"));

        await coordinator.PrepareCloseAsync(
            () => Task.FromException(new InvalidOperationException("fixture failure")),
            () => closeRequests++,
            () => shutdownRequests++);
        Assert.AreEqual(
            MainWindowClosingDecision.Close,
            coordinator.EvaluateClosing(false, true, () => true, "player"));
        coordinator.CompleteClose(false, "player", () => shutdownRequests++);

        Assert.AreEqual(1, closeRequests);
        Assert.AreEqual(1, shutdownRequests);
        Assert.IsTrue(diagnostics.Entries.Any(entry =>
            entry.Contains("event=close-cleanup-failed", StringComparison.Ordinal)
            && entry.Contains("exceptionType=InvalidOperationException", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Entries.Any(entry => entry.Contains("event=closed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NormalClose_CleansUpThenRequestsShutdownExactlyOnce()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);
        var cleanupCalls = 0;
        var closeRequests = 0;
        var shutdownRequests = 0;
        Assert.AreEqual(
            MainWindowClosingDecision.Prepare,
            coordinator.EvaluateClosing(false, false, () => true, "home"));

        await coordinator.PrepareCloseAsync(
            () =>
            {
                cleanupCalls++;
                return Task.CompletedTask;
            },
            () => closeRequests++,
            () => shutdownRequests++);
        coordinator.CompleteClose(false, "home", () => shutdownRequests++);
        coordinator.CompleteClose(false, "home", () => shutdownRequests++);

        Assert.AreEqual(1, cleanupCalls);
        Assert.AreEqual(1, closeRequests);
        Assert.AreEqual(1, shutdownRequests);
        Assert.AreEqual(1, diagnostics.Entries.Count(entry =>
            entry.Contains("event=closed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PrepareClose_WhileCleanupIsPending_CancelsSecondCloseUntilCompletion()
    {
        var coordinator = new MainWindowLifecycleCoordinator(new RecordingDiagnostics());
        var cleanupCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeRequests = 0;
        Assert.AreEqual(
            MainWindowClosingDecision.Prepare,
            coordinator.EvaluateClosing(false, true, () => true, "player"));

        var preparation = coordinator.PrepareCloseAsync(
            () => cleanupCompletion.Task,
            () => closeRequests++,
            () => Assert.Fail("Normal preparation must not request shutdown."));

        Assert.AreEqual(
            MainWindowClosingDecision.Cancel,
            coordinator.EvaluateClosing(false, true, () => true, "player"));
        Assert.AreEqual(0, closeRequests);

        cleanupCompletion.SetResult();
        await preparation;

        Assert.AreEqual(1, closeRequests);
        Assert.AreEqual(
            MainWindowClosingDecision.Close,
            coordinator.EvaluateClosing(false, true, () => true, "player"));
    }

    [TestMethod]
    public async Task DestroyDuringNormalPreparation_ReusesCleanupAndShutsDownWithoutRequestingClose()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);
        var cleanupCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCalls = 0;
        var closeRequests = 0;
        var shutdownRequests = 0;
        Assert.AreEqual(
            MainWindowClosingDecision.Prepare,
            coordinator.EvaluateClosing(false, true, () => true, "home"));

        var preparation = coordinator.PrepareCloseAsync(
            async () =>
            {
                cleanupCalls++;
                await cleanupCompletion.Task;
            },
            () => closeRequests++,
            () => shutdownRequests++);

        coordinator.RecordNativeMessage(
            MainWindowNativeMessage.WmDestroy,
            normalCloseAccepted: false);
        Assert.AreEqual(
            UnexpectedWindowDestructionDecision.AwaitPreparation,
            coordinator.EvaluateUnexpectedDestruction(
                MainWindowDestructionTrigger.WmDestroy,
                normalCloseAccepted: false,
                dispatcherShuttingDown: false));
        Assert.AreEqual(
            UnexpectedWindowDestructionDecision.Ignore,
            coordinator.EvaluateUnexpectedDestruction(
                MainWindowDestructionTrigger.WmNcDestroy,
                normalCloseAccepted: false,
                dispatcherShuttingDown: false));

        cleanupCompletion.SetResult();
        await preparation;

        Assert.AreEqual(1, cleanupCalls);
        Assert.AreEqual(0, closeRequests);
        Assert.AreEqual(1, shutdownRequests);
        Assert.AreEqual(1, diagnostics.Entries.Count(entry =>
            entry.Contains("event=unexpected-window-destroy", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Entries.Any(entry =>
            entry.Contains("message=WM_DESTROY normalClose=false preparing=true", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NormalCloseNativeMessage_DoesNotStartUnexpectedFallback()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);

        coordinator.RecordNativeMessage(
            MainWindowNativeMessage.WmClose,
            normalCloseAccepted: true);
        var decision = coordinator.EvaluateUnexpectedDestruction(
            MainWindowDestructionTrigger.WmDestroy,
            normalCloseAccepted: true,
            dispatcherShuttingDown: false);

        Assert.AreEqual(UnexpectedWindowDestructionDecision.Ignore, decision);
        Assert.IsFalse(coordinator.IsClosePreparationStarted);
        Assert.AreEqual(1, diagnostics.Entries.Count);
        StringAssert.Contains(diagnostics.Entries[0], "message=WM_CLOSE");
        StringAssert.Contains(diagnostics.Entries[0], "normalClose=true");
        Assert.IsFalse(diagnostics.Entries.Any(entry =>
            entry.Contains("event=unexpected-window-destroy", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UnexpectedNcDestroy_CleansUpAndRequestsShutdownExactlyOnce()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);
        var cleanupCalls = 0;
        var shutdownRequests = 0;

        var decision = coordinator.EvaluateUnexpectedDestruction(
            MainWindowDestructionTrigger.WmNcDestroy,
            normalCloseAccepted: false,
            dispatcherShuttingDown: false);
        Assert.AreEqual(UnexpectedWindowDestructionDecision.Prepare, decision);

        await coordinator.PrepareUnexpectedCloseAsync(
            () =>
            {
                cleanupCalls++;
                return Task.CompletedTask;
            },
            () => shutdownRequests++);

        Assert.AreEqual(
            UnexpectedWindowDestructionDecision.Ignore,
            coordinator.EvaluateUnexpectedDestruction(
                MainWindowDestructionTrigger.HwndSourceDisposed,
                normalCloseAccepted: false,
                dispatcherShuttingDown: false));
        coordinator.CompleteUnexpectedCloseWithoutDispatch();

        Assert.AreEqual(1, cleanupCalls);
        Assert.AreEqual(1, shutdownRequests);
        Assert.IsTrue(coordinator.IsClosePrepared);
        Assert.AreEqual(1, diagnostics.Entries.Count(entry =>
            entry.Contains("event=unexpected-window-destroy", StringComparison.Ordinal)));
        Assert.AreEqual(1, diagnostics.Entries.Count(entry =>
            entry.Contains("event=unexpected-window-shutdown", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UnexpectedDestroy_WhenDispatcherIsStopping_CompletesSafelyWithoutSchedulingCleanup()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);

        var decision = coordinator.EvaluateUnexpectedDestruction(
            MainWindowDestructionTrigger.WmDestroy,
            normalCloseAccepted: false,
            dispatcherShuttingDown: true);
        coordinator.CompleteUnexpectedCloseWithoutDispatch();

        Assert.AreEqual(UnexpectedWindowDestructionDecision.DispatcherStopping, decision);
        Assert.IsTrue(coordinator.IsClosePrepared);
        Assert.AreEqual(1, diagnostics.Entries.Count(entry =>
            entry.Contains("event=unexpected-window-destroy", StringComparison.Ordinal)));
        Assert.AreEqual(1, diagnostics.Entries.Count(entry =>
            entry.Contains("event=unexpected-window-shutdown", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LifecycleEvents_RecordOnlyFixedStateAndNativeMessageFields()
    {
        var diagnostics = new RecordingDiagnostics();
        var coordinator = new MainWindowLifecycleCoordinator(diagnostics);

        coordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.SourceInitialized);
        coordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.Loaded);
        coordinator.RecordLifecycleEvent(
            MainWindowLifecycleEvent.VisibilityChanged,
            isVisible: true);
        coordinator.RecordNativeMessage(
            MainWindowNativeMessage.WmNcDestroy,
            normalCloseAccepted: false);
        coordinator.RecordLifecycleEvent(
            MainWindowLifecycleEvent.HwndSourceDisposed,
            normalCloseAccepted: false);
        coordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.Unloaded);
        coordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.DispatcherShutdownStarted);
        coordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.DispatcherShutdownFinished);

        CollectionAssert.AreEqual(
            new[]
            {
                "category=window event=source-initialized ",
                "category=window event=loaded ",
                "category=window event=visibility-changed isVisible=true",
                "category=window event=native-message message=WM_NCDESTROY normalClose=false preparing=false",
                "category=window event=hwnd-source-disposed normalClose=false preparing=false",
                "category=window event=unloaded ",
                "category=window event=dispatcher-shutdown-started ",
                "category=window event=dispatcher-shutdown-finished "
            },
            diagnostics.Entries);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class RecordingDiagnostics : IApplicationDiagnostics
    {
        public List<string> Entries { get; } = new();

        public void Write(string category, string eventName, string? details = null)
        {
            Entries.Add($"category={category} event={eventName} {details}");
        }

        public Task<bool> FlushAsync(TimeSpan timeout) => Task.FromResult(true);
    }
}
