[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$workspaceRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$executableFull = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $executableFull -PathType Leaf)) {
    throw "Lifecycle verification executable is missing."
}

if (-not ("LifecycleWindowProbe" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class LifecycleWindowProbe
{
    private delegate bool EnumWindowsProcedure(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    public static IntPtr FindVisibleTopLevelWindow(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows((windowHandle, _) =>
        {
            if (IsExpectedWindow(windowHandle, processId))
            {
                found = windowHandle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool TryPostClose(int processId, IntPtr windowHandle) =>
        IsExpectedWindow(windowHandle, processId) &&
        PostMessage(windowHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);

    private static bool IsExpectedWindow(IntPtr windowHandle, int processId)
    {
        uint ownerProcessId;
        if (GetWindowThreadProcessId(windowHandle, out ownerProcessId) == 0 ||
            ownerProcessId != (uint)processId ||
            !IsWindowVisible(windowHandle))
        {
            return false;
        }

        var title = new StringBuilder(64);
        GetWindowText(windowHandle, title, title.Capacity);
        return title.ToString() == "Jeby Player";
    }
}
"@
}

$testRoot = Join-Path $workspaceRoot (".tmp\window-lifecycle-process\case-" + [Guid]::NewGuid().ToString("N"))
[void] [System.IO.Directory]::CreateDirectory($testRoot)
$verificationSucceeded = $false
try {
    foreach ($case in @(
            [pscustomobject]@{ Name = "ordinary"; Arguments = "" },
            [pscustomobject]@{ Name = "release-smoke"; Arguments = "--release-smoke" })) {
        $caseRoot = Join-Path $testRoot $case.Name
        [void] [System.IO.Directory]::CreateDirectory($caseRoot)
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $executableFull
        $startInfo.Arguments = $case.Arguments
        $startInfo.WorkingDirectory = Split-Path -Parent $executableFull
        $startInfo.UseShellExecute = $false
        $startInfo.EnvironmentVariables["EMBYPLAYER_TEST_DATA_ROOT"] = $caseRoot
        $process = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw "Unable to start lifecycle verification case: $($case.Name)"
        }

        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            $windowHandle = [IntPtr]::Zero
            while ([DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
                $process.Refresh()
                if ($process.HasExited) {
                    throw "Lifecycle case $($case.Name) exited before showing a window."
                }

                $windowHandle = [LifecycleWindowProbe]::FindVisibleTopLevelWindow($process.Id)
                if ($windowHandle -ne [IntPtr]::Zero) {
                    break
                }
            }

            if ($windowHandle -eq [IntPtr]::Zero) {
                throw "Lifecycle case $($case.Name) did not show a visible window."
            }

            $stabilityDeadline = [DateTime]::UtcNow.AddSeconds(8)
            while ([DateTime]::UtcNow -lt $stabilityDeadline) {
                Start-Sleep -Milliseconds 200
                $process.Refresh()
                if ($process.HasExited) {
                    throw "Lifecycle case $($case.Name) exited during the stable-window observation."
                }

                $windowHandle = [LifecycleWindowProbe]::FindVisibleTopLevelWindow($process.Id)
                if ($windowHandle -eq [IntPtr]::Zero) {
                    throw "Lifecycle case $($case.Name) lost its visible top-level window during startup."
                }
            }

            if (-not [LifecycleWindowProbe]::TryPostClose($process.Id, $windowHandle)) {
                throw "Lifecycle case $($case.Name) could not post the normal close message to its app window."
            }

            if (-not $process.WaitForExit(5000)) {
                throw "Lifecycle case $($case.Name) remained alive after normal window close."
            }

            if ($process.ExitCode -ne 0) {
                throw "Lifecycle case $($case.Name) returned a non-zero exit code."
            }

            $logLines = @()
            try {
                foreach ($logName in @("application.log", "application.previous.log")) {
                    $logPath = Join-Path (Join-Path $caseRoot "logs") $logName
                    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                        $logLines += @(Get-Content -LiteralPath $logPath)
                    }
                }
            }
            catch {
                throw "Lifecycle case $($case.Name) diagnostics could not be read."
            }

            foreach ($expectedEvent in @(
                    [pscustomobject]@{ Name = "startup"; Pattern = "category=application event=startup" },
                    [pscustomobject]@{ Name = "closing"; Pattern = "category=application event=closing cancelled=false" },
                    [pscustomobject]@{ Name = "closed"; Pattern = "category=application event=closed" },
                    [pscustomobject]@{ Name = "exit"; Pattern = "category=application event=exit" })) {
                $eventCount = @($logLines | Where-Object {
                        $_.IndexOf($expectedEvent.Pattern, [StringComparison]::Ordinal) -ge 0
                    }).Count
                if ($eventCount -ne 1) {
                    throw "Lifecycle case $($case.Name) has an invalid $($expectedEvent.Name) event count."
                }
            }

            $errorCount = @($logLines | Where-Object {
                    $_ -match '(?i)(?:\bevent=[^\s]*(?:error|failed)\b|\berror=|\bexceptionType=)'
                }).Count
            if ($errorCount -ne 0) {
                throw "Lifecycle case $($case.Name) emitted error diagnostics."
            }

            Write-Output "LIFECYCLE-PASSED:$($case.Name)"
        }
        finally {
            $process.Refresh()
            if (-not $process.HasExited) {
                $process.Kill()
                [void] $process.WaitForExit(5000)
            }

            $process.Dispose()
        }
    }

    $verificationSucceeded = $true
}
finally {
    $testRootFull = [System.IO.Path]::GetFullPath($testRoot)
    $testBaseFull = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot ".tmp\window-lifecycle-process"))
    $testBasePrefix = $testBaseFull.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($verificationSucceeded -and
        $testRootFull.StartsWith($testBasePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $testRootFull -PathType Container)) {
        Remove-Item -LiteralPath $testRootFull -Recurse -Force
    }
    elseif (-not $verificationSucceeded) {
        Write-Warning "LIFECYCLE-DIAGNOSTICS-RETAINED"
    }
}
