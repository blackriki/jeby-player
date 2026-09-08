using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Automation;
using EmbyPlayer.Core.Servers;
using EmbyPlayer.Emby;

internal static partial class Program
{
    private static async Task RunUiSmokeAsync(string root, string outputRoot, CheckResult result, CancellationToken cancellationToken)
    {
        result.Details["scope"] = "owned-isolated-process-connection-page";
        result.Details["network"] = "NotRun";
        result.Details["screenshots"] = "NotRun";
        result.Details["releaseSmoke"] = false;
        // Prove this exact artifact rejects the fixture before invoking HTTP, then use it in the UI.
        using var http = new HttpClient(new RejectNetworkHandler());
        var invalidAddress = await new EmbyServerConnectionService(http).ConnectAsync("://", cancellationToken);
        result.Assert("ui-invalid-address-local-validation", invalidAddress.Error == ServerConnectionError.InvalidUrl);
        var executable = Path.Combine(root, "EmbyPlayer.App.exe");
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "release-manifest.json"))))
        {
            var entry = manifest.RootElement.GetProperty("files").EnumerateArray().Single(
                item => item.GetProperty("relativePath").GetString() == "EmbyPlayer.App.exe");
            result.Assert("ui-executable-binding", HashFile(executable).Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)
                && new FileInfo(executable).Length == entry.GetProperty("sizeBytes").GetInt64());
        }
        var dataRoot = Path.Combine(outputRoot, "ui-app-data");
        Directory.CreateDirectory(dataRoot);
        result.Assert("ui-fresh-data-directory", !Directory.EnumerateFileSystemEntries(dataRoot).Any());
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = root };
        start.Environment["EMBYPLAYER_TEST_DATA_ROOT"] = dataRoot;
        using var process = Process.Start(start) ?? throw new CheckFailure("ui-process-start");
        result.Details["processId"] = process.Id;
        result.Details["processSessionId"] = process.SessionId;
        result.Details["processStartedAtUtc"] = process.StartTime.ToUniversalTime();
        try
        {
            AutomationElement? window = null;
            await WaitForAsync(() =>
            {
                Require(!process.HasExited, "ui-process-exited-before-window");
                window = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                    new PropertyCondition(AutomationElement.NameProperty, "Jeby Player")));
                return window is not null;
            }, cancellationToken);
            result.Assert("ui-owned-window", window!.Current.ProcessId == process.Id
                && string.Equals(process.MainModule!.FileName, executable, StringComparison.OrdinalIgnoreCase));
            // RC payloads predate explicit IDs here. Type-only selection is allowed only for one edit control.
            var address = await FindControlAsync(window, process.Id, ControlType.Edit, null, cancellationToken);
            var connect = await FindControlAsync(window, process.Id, ControlType.Button, "连接", cancellationToken);
            var value = (ValuePattern)address.GetCurrentPattern(ValuePattern.Pattern);
            result.Assert("ui-empty-address", !value.Current.IsReadOnly && value.Current.Value.Length == 0 && !connect.Current.IsEnabled);
            value.SetValue("://");
            await WaitForAsync(() => value.Current.Value == "://" && connect.Current.IsEnabled, cancellationToken);
            ((InvokePattern)connect.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            _ = await FindControlAsync(window, process.Id, ControlType.Text, "请输入有效的服务器地址", cancellationToken);
            await WaitForAsync(() => address.Current.IsEnabled && connect.Current.IsEnabled, cancellationToken);
            result.Assert("ui-invalid-address-feedback", address.Current.IsEnabled && connect.Current.IsEnabled);
            value.SetValue(string.Empty);
            await WaitForAsync(() => !connect.Current.IsEnabled, cancellationToken);
            result.Assert("ui-address-cleared", value.Current.Value.Length == 0 && !connect.Current.IsEnabled);
            var close = await FindControlAsync(window, process.Id, ControlType.Button, "关闭", cancellationToken);
            ((InvokePattern)close.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await process.WaitForExitAsync(cancellationToken);
            result.Assert("ui-semantic-close", process.ExitCode == 0);
        }
        finally
        {
            // Never close or terminate an existing app instance; only request graceful close of our child.
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(cleanupTimeout.Token); }
                catch (OperationCanceledException) { result.Details["processLeftRunning"] = process.Id; }
            }
            if (process.HasExited) result.Details["processExitCode"] = process.ExitCode;
        }
    }

    private static async Task<AutomationElement> FindControlAsync(AutomationElement window, int processId,
        ControlType type, string? name, CancellationToken cancellationToken)
    {
        AutomationElement? match = null;
        await WaitForAsync(() =>
        {
            var conditions = new List<Condition>
            {
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.ControlTypeProperty, type)
            };
            if (name is not null) conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name));
            var matches = window.FindAll(TreeScope.Descendants, new AndCondition(conditions.ToArray()));
            Require(matches.Count <= 1, "ui-selector-ambiguous");
            match = matches.Count == 1 ? matches[0] : null;
            return match is not null && !match.Current.IsOffscreen;
        }, cancellationToken);
        return match!;
    }

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate()) return;
            await Task.Delay(100, cancellationToken);
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new CheckFailure("ui-fixture-attempted-network");
    }
}
