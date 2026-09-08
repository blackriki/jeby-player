using System.Windows;
using EmbyPlayer.Core.Diagnostics;

namespace EmbyPlayer.App;

public partial class App : Application
{
    private static readonly TimeSpan DiagnosticFlushTimeout = TimeSpan.FromSeconds(1);

    protected override void OnStartup(StartupEventArgs e)
    {
        ApplicationDiagnosticLog.Shared.Write("application", "startup");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ApplicationDiagnosticLog.Shared.Write("application", "exit");
            _ = ApplicationDiagnosticLog.Shared
                .FlushAsync(DiagnosticFlushTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Application diagnostics flush failed: {exception.GetType().Name}");
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
