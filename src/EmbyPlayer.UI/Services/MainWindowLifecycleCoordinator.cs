using EmbyPlayer.Core.Diagnostics;

namespace EmbyPlayer.UI.Services;

public enum MainWindowClosingDecision
{
    Cancel,
    Prepare,
    Close
}

public enum UnexpectedWindowDestructionDecision
{
    Ignore,
    Prepare,
    AwaitPreparation,
    ShutdownPrepared,
    DispatcherStopping
}

public enum MainWindowNativeMessage
{
    WmClose,
    WmDestroy,
    WmNcDestroy
}

public enum MainWindowLifecycleEvent
{
    SourceInitialized,
    Loaded,
    Unloaded,
    VisibilityChanged,
    HwndSourceDisposed,
    DispatcherShutdownStarted,
    DispatcherShutdownFinished
}

public enum MainWindowDestructionTrigger
{
    WmDestroy,
    WmNcDestroy,
    HwndSourceDisposed
}

public sealed class MainWindowLifecycleCoordinator
{
    private readonly IApplicationDiagnostics diagnostics;
    private bool closePreparationStarted;
    private bool closePrepared;
    private bool shutdownRequested;
    private bool unexpectedDestructionObserved;

    public MainWindowLifecycleCoordinator(IApplicationDiagnostics diagnostics)
    {
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public bool IsClosePreparationStarted => closePreparationStarted;

    public bool IsClosePrepared => closePrepared;

    public MainWindowClosingDecision EvaluateClosing(
        bool isReleaseSmoke,
        bool bypassSettingsCloseGuard,
        Func<bool> requestSettingsClose,
        string pageCategory)
    {
        if (closePrepared)
        {
            return MainWindowClosingDecision.Close;
        }

        if (closePreparationStarted)
        {
            return MainWindowClosingDecision.Cancel;
        }

        if (!isReleaseSmoke
            && !bypassSettingsCloseGuard
            && !requestSettingsClose())
        {
            WriteClosing(cancelled: true, isReleaseSmoke, pageCategory);
            return MainWindowClosingDecision.Cancel;
        }

        closePreparationStarted = true;
        WriteClosing(cancelled: false, isReleaseSmoke, pageCategory);
        return MainWindowClosingDecision.Prepare;
    }

    public async Task PrepareCloseAsync(
        Func<Task> cleanup,
        Action requestClose,
        Action requestShutdown)
    {
        try
        {
            await cleanup().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            RecordCleanupFailure("pre-close", exception);
        }
        finally
        {
            closePrepared = true;
            if (unexpectedDestructionObserved)
            {
                CompleteUnexpectedPreparedClose(requestShutdown);
            }
            else
            {
                requestClose();
            }
        }
    }

    public UnexpectedWindowDestructionDecision EvaluateUnexpectedDestruction(
        MainWindowDestructionTrigger trigger,
        bool normalCloseAccepted,
        bool dispatcherShuttingDown)
    {
        if (normalCloseAccepted || shutdownRequested || unexpectedDestructionObserved)
        {
            return UnexpectedWindowDestructionDecision.Ignore;
        }

        unexpectedDestructionObserved = true;
        diagnostics.Write(
            "application",
            "unexpected-window-destroy",
            $"trigger={GetDestructionTriggerName(trigger)} "
            + $"dispatcherShuttingDown={dispatcherShuttingDown.ToString().ToLowerInvariant()}");

        if (!dispatcherShuttingDown)
        {
            if (closePreparationStarted)
            {
                return closePrepared
                    ? UnexpectedWindowDestructionDecision.ShutdownPrepared
                    : UnexpectedWindowDestructionDecision.AwaitPreparation;
            }

            closePreparationStarted = true;
            return UnexpectedWindowDestructionDecision.Prepare;
        }

        CompleteUnexpectedCloseWithoutDispatch();
        return UnexpectedWindowDestructionDecision.DispatcherStopping;
    }

    public async Task PrepareUnexpectedCloseAsync(Func<Task> cleanup, Action requestShutdown)
    {
        try
        {
            await cleanup().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            RecordCleanupFailure("unexpected-pre-close", exception);
        }
        finally
        {
            closePrepared = true;
            CompleteShutdown(
                "unexpected-window-shutdown",
                "dispatcherShuttingDown=false",
                requestShutdown);
        }
    }

    public void CompleteUnexpectedCloseWithoutDispatch()
    {
        closePrepared = true;
        CompleteShutdown(
            "unexpected-window-shutdown",
            "dispatcherShuttingDown=true",
            static () => { });
    }

    public void CompleteUnexpectedPreparedClose(Action requestShutdown)
    {
        CompleteShutdown(
            "unexpected-window-shutdown",
            "dispatcherShuttingDown=false",
            requestShutdown);
    }

    public void RecordLifecycleEvent(
        MainWindowLifecycleEvent lifecycleEvent,
        bool? isVisible = null,
        bool normalCloseAccepted = false)
    {
        var details = lifecycleEvent switch
        {
            MainWindowLifecycleEvent.VisibilityChanged =>
                $"isVisible={isVisible.GetValueOrDefault().ToString().ToLowerInvariant()}",
            MainWindowLifecycleEvent.HwndSourceDisposed =>
                $"normalClose={normalCloseAccepted.ToString().ToLowerInvariant()} "
                + $"preparing={closePreparationStarted.ToString().ToLowerInvariant()}",
            _ => null
        };
        diagnostics.Write("window", GetLifecycleEventName(lifecycleEvent), details);
    }

    public void RecordNativeMessage(
        MainWindowNativeMessage message,
        bool normalCloseAccepted)
    {
        diagnostics.Write(
            "window",
            "native-message",
            $"message={GetNativeMessageName(message)} "
            + $"normalClose={normalCloseAccepted.ToString().ToLowerInvariant()} "
            + $"preparing={closePreparationStarted.ToString().ToLowerInvariant()}");
    }

    public void RecordCleanupFailure(string phase, Exception exception)
    {
        diagnostics.Write(
            "application",
            "close-cleanup-failed",
            $"phase={phase} exceptionType={exception.GetType().Name}");
    }

    public void CompleteClose(
        bool isReleaseSmoke,
        string pageCategory,
        Action requestShutdown)
    {
        if (shutdownRequested)
        {
            return;
        }

        CompleteShutdown(
            "closed",
            $"releaseSmoke={isReleaseSmoke.ToString().ToLowerInvariant()} "
            + $"pageCategory={pageCategory}",
            requestShutdown);
    }

    private void WriteClosing(bool cancelled, bool isReleaseSmoke, string pageCategory)
    {
        diagnostics.Write(
            "application",
            "closing",
            $"cancelled={cancelled.ToString().ToLowerInvariant()} "
            + $"releaseSmoke={isReleaseSmoke.ToString().ToLowerInvariant()} "
            + $"pageCategory={pageCategory}");
    }

    private void CompleteShutdown(string eventName, string details, Action requestShutdown)
    {
        if (shutdownRequested)
        {
            return;
        }

        shutdownRequested = true;
        diagnostics.Write("application", eventName, details);
        try
        {
            requestShutdown();
        }
        catch (Exception exception)
        {
            RecordCleanupFailure("application-shutdown", exception);
        }
    }

    private static string GetNativeMessageName(MainWindowNativeMessage message) => message switch
    {
        MainWindowNativeMessage.WmClose => "WM_CLOSE",
        MainWindowNativeMessage.WmDestroy => "WM_DESTROY",
        MainWindowNativeMessage.WmNcDestroy => "WM_NCDESTROY",
        _ => throw new ArgumentOutOfRangeException(nameof(message))
    };

    private static string GetLifecycleEventName(MainWindowLifecycleEvent lifecycleEvent) => lifecycleEvent switch
    {
        MainWindowLifecycleEvent.SourceInitialized => "source-initialized",
        MainWindowLifecycleEvent.Loaded => "loaded",
        MainWindowLifecycleEvent.Unloaded => "unloaded",
        MainWindowLifecycleEvent.VisibilityChanged => "visibility-changed",
        MainWindowLifecycleEvent.HwndSourceDisposed => "hwnd-source-disposed",
        MainWindowLifecycleEvent.DispatcherShutdownStarted => "dispatcher-shutdown-started",
        MainWindowLifecycleEvent.DispatcherShutdownFinished => "dispatcher-shutdown-finished",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycleEvent))
    };

    private static string GetDestructionTriggerName(MainWindowDestructionTrigger trigger) => trigger switch
    {
        MainWindowDestructionTrigger.WmDestroy => "WM_DESTROY",
        MainWindowDestructionTrigger.WmNcDestroy => "WM_NCDESTROY",
        MainWindowDestructionTrigger.HwndSourceDisposed => "HwndSource.Disposed",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };
}
