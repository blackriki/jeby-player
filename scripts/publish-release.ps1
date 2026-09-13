[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9][0-9A-Za-z.-]{0,63}$')]
    [string] $Version,
    [ValidateSet("FrameworkDependent", "SelfContained")]
    [string] $Deployment = "FrameworkDependent",
    [ValidateSet("Internal", "Public")]
    [string] $Channel = "Internal",
    [string] $OutputRoot = ".tmp\release-staging",
    [switch] $AllowDirty,
    [switch] $SkipLaunchSmoke,
    [switch] $SkipTests,
    [switch] $PreflightOnly,
    [switch] $DirectoryOnly,
    [switch] $SafeLaunchSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$workspaceRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$workspacePrefix = $workspaceRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$appProject = Join-Path $workspaceRoot "src\EmbyPlayer.App\EmbyPlayer.App.csproj"
$mpvManifestPath = Join-Path $workspaceRoot "src\EmbyPlayer.App\runtimes\win-x64\native\mpv-runtime.json"
$sensitiveScript = Join-Path $workspaceRoot "scripts\check-sensitive-files.ps1"
$buildTestScript = Join-Path $workspaceRoot "scripts\build-test.ps1"
$validationScript = Join-Path $workspaceRoot "scripts\release-validation.ps1"
. $validationScript

function Test-IsInsideWorkspace([string] $path) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    return $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePointInWorkspacePath([string] $path) {
    $current = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($path))
    while ($null -ne $current) {
        if ($current.Exists) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (-not $item.PSIsContainer) {
                throw "Expected a directory but found another item type: $($current.FullName)"
            }

            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release paths must not traverse a reparse point: $($current.FullName)"
            }
        }

        if ($current.FullName.Equals($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        $current = $current.Parent
    }

    throw "Release path did not resolve beneath the workspace."
}

function Invoke-GitCapture([string[]] $Arguments, [string] $Operation) {
    $output = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }

    return $output
}

function Invoke-DotNet([string[]] $Arguments, [string] $Operation) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Invoke-LaunchSmoke(
    [string] $executablePath,
    [bool] $AllowForceTermination) {
    if (-not ("ReleaseWindowProbe" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ReleaseWindowProbe
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

    $smokeProcess = $null
    $smokeWindowHandle = [IntPtr]::Zero
    try {
        $smokeProcess = Start-Process `
            -FilePath $executablePath `
            -ArgumentList "--release-smoke" `
            -WorkingDirectory (Split-Path -Parent $executablePath) `
            -PassThru
        $smokePid = $smokeProcess.Id
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $visible = $false
        do {
            Start-Sleep -Milliseconds 200
            $smokeProcess.Refresh()
            if ($smokeProcess.HasExited) {
                throw "Launch smoke process $smokePid exited before showing a window (exit code $($smokeProcess.ExitCode))."
            }

            $smokeWindowHandle = [ReleaseWindowProbe]::FindVisibleTopLevelWindow($smokePid)
            if ($smokeWindowHandle -ne [IntPtr]::Zero) {
                $visible = $true
                break
            }
        } while ([DateTime]::UtcNow -lt $deadline)

        if (-not $visible) {
            throw "Launch smoke process $smokePid did not show a visible top-level window within 20 seconds."
        }

        $stabilityDeadline = [DateTime]::UtcNow.AddSeconds(8)
        while ([DateTime]::UtcNow -lt $stabilityDeadline) {
            Start-Sleep -Milliseconds 200
            $smokeProcess.Refresh()
            if ($smokeProcess.HasExited) {
                throw "Launch smoke process $smokePid exited during the stable-window observation."
            }

            $smokeWindowHandle = [ReleaseWindowProbe]::FindVisibleTopLevelWindow($smokePid)
            if ($smokeWindowHandle -eq [IntPtr]::Zero) {
                throw "Launch smoke process $smokePid lost its visible top-level window during startup."
            }
        }

        return [ordered]@{
            status = "passed"
            processId = $smokePid
            visibleTopLevelWindow = $true
        }
    }
    finally {
        if ($null -ne $smokeProcess) {
            try {
                Complete-ReleaseLaunchSmokeProcess `
                    -Process $smokeProcess `
                    -AllowForceTermination $AllowForceTermination `
                    -CloseAppWindow {
                        if (-not [ReleaseWindowProbe]::TryPostClose($smokePid, $smokeWindowHandle)) {
                            throw "Launch smoke process $smokePid no longer owns the verified app window."
                        }
                    }
            }
            finally {
                $smokeProcess.Dispose()
            }
        }
    }
}

function Remove-OwnedWorkingDirectory(
    [string] $Path,
    [string] $ExpectedPath,
    [string] $OutputRoot) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $expectedFullPath = [System.IO.Path]::GetFullPath($ExpectedPath)
    if (-not $fullPath.Equals($expectedFullPath, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-ReleasePathInside -Root $OutputRoot -Path $fullPath) -or -not ([System.IO.Path]::GetFileName($fullPath)).StartsWith(".working-", [StringComparison]::Ordinal)) {
        throw "Refusing to remove an unowned release working directory: $fullPath"
    }

    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $rootItem = Get-Item -LiteralPath $fullPath -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove an unsafe release working directory: $fullPath"
    }

    Assert-NoReparsePointTree -Root $fullPath
    Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
}

function Remove-OwnedTemporaryZip(
    [string] $Path,
    [string] $ExpectedPath,
    [string] $OutputRoot) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $expectedFullPath = [System.IO.Path]::GetFullPath($ExpectedPath)
    if (-not $fullPath.Equals($expectedFullPath, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-ReleasePathInside -Root $OutputRoot -Path $fullPath) -or -not ([System.IO.Path]::GetFileName($fullPath)).StartsWith(".working-", [StringComparison]::Ordinal)) {
        throw "Refusing to remove an unowned release temporary ZIP: $fullPath"
    }

    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove an unsafe release temporary ZIP: $fullPath"
    }

    Remove-Item -LiteralPath $fullPath -Force -ErrorAction Stop
}

function Remove-OwnedWorkingChildDirectory(
    [string] $Path,
    [string] $WorkingRoot) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $workingRootFull = [System.IO.Path]::GetFullPath($WorkingRoot)
    $parentPath = [System.IO.Directory]::GetParent($fullPath)
    if ($null -eq $parentPath -or -not $parentPath.FullName.Equals($workingRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory that is not a direct child of this release transaction: $fullPath"
    }

    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $item = Get-Item -LiteralPath $fullPath -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove an unsafe release transaction directory: $fullPath"
    }

    Assert-NoReparsePointTree -Root $fullPath
    Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
}

if ($Version.IndexOf("..", [StringComparison]::Ordinal) -ge 0 -or $Version.EndsWith(".", [StringComparison]::Ordinal)) {
    throw "Version contains an unsafe path segment."
}

if ($Channel -eq "Public" -and $SkipLaunchSmoke) {
    throw "Public releases cannot skip launch smoke verification."
}

if ($Channel -eq "Public" -and $AllowDirty) {
    throw "Public releases never allow -AllowDirty."
}

if ($Channel -eq "Public" -and $SkipTests) {
    throw "Public releases cannot skip tests."
}

if ($Channel -eq "Public" -and $DirectoryOnly) {
    throw "Public releases must produce an immutable ZIP archive."
}

if ($Channel -eq "Public" -and $SafeLaunchSmoke) {
    throw "Public releases cannot use the non-force launch smoke mode."
}

$gitRootOutput = @(Invoke-GitCapture -Arguments @("rev-parse", "--show-toplevel") -Operation "git root")
$gitRoot = [System.IO.Path]::GetFullPath($gitRootOutput[0])
if (-not $gitRoot.Equals($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "publish-release.ps1 must run from the repository workspace."
}

$branch = (Invoke-GitCapture -Arguments @("branch", "--show-current") -Operation "git branch") -join ""
if ([string]::IsNullOrWhiteSpace($branch)) {
    $branch = "(detached)"
}

$commit = ((Invoke-GitCapture -Arguments @("rev-parse", "HEAD") -Operation "git commit") -join "").Trim()
$statusLines = @(Invoke-GitCapture -Arguments @("status", "--porcelain=v1", "--untracked-files=all") -Operation "git status")
$workingTreeDirty = $statusLines.Count -gt 0
if ($Channel -eq "Public" -and $workingTreeDirty) {
    throw "Public releases require a clean working tree."
}

if ($workingTreeDirty -and -not $AllowDirty) {
    throw "The working tree is dirty. Commit the changes or pass -AllowDirty for an explicitly marked internal build."
}

& $sensitiveScript
if (-not $?) {
    throw "Sensitive-file validation failed."
}

if (-not (Test-Path -LiteralPath $mpvManifestPath -PathType Leaf)) {
    throw "Tracked MPV runtime manifest is missing: $mpvManifestPath"
}

$mpvManifest = Get-Content -LiteralPath $mpvManifestPath -Raw | ConvertFrom-Json
$mpvManifestDirectory = Split-Path -Parent $mpvManifestPath
$sourceMpvPath = Join-Path $mpvManifestDirectory ([string] $mpvManifest.binary.fileName)
$mpvRuntime = Assert-MpvRuntime -binaryPath $sourceMpvPath -manifest $mpvManifest
$mpvDependencies = @(Assert-MpvRuntimeDependencies -RuntimeDirectory (Split-Path -Parent $sourceMpvPath) -Manifest $mpvManifest -RequireExactFileSet)
$mpvLicenseEvidence = @(
    Get-MpvLicenseEvidence -Manifest $mpvManifest -ManifestPath $mpvManifestPath -WorkspaceRoot $workspaceRoot
)
$mpvPublicReady = Test-MpvPublicDistributionReady -Manifest $mpvManifest -LicenseEvidence $mpvLicenseEvidence
if ($Channel -eq "Public" -and -not $mpvPublicReady) {
    throw "Public release is blocked: MPV source provenance, build recipe revision, and license evidence are incomplete."
}

if ($Channel -eq "Internal" -and -not $mpvPublicReady) {
    Write-Warning "MPV provenance and license evidence are incomplete. This package is internal-only and distributionReady=false."
}

$expectedLockFiles = @(
    "src\EmbyPlayer.App\packages.lock.json",
    "src\EmbyPlayer.Core\packages.lock.json",
    "src\EmbyPlayer.Emby\packages.lock.json",
    "src\EmbyPlayer.Player\packages.lock.json",
    "src\EmbyPlayer.UI\packages.lock.json",
    "tests\EmbyPlayer.Core.Tests\packages.lock.json",
    "tests\EmbyPlayer.Emby.Tests\packages.lock.json",
    "tests\EmbyPlayer.Player.Tests\packages.lock.json",
    "tests\EmbyPlayer.UI.Tests\packages.lock.json"
)
foreach ($relativeLockFile in $expectedLockFiles) {
    $lockFile = Join-Path $workspaceRoot $relativeLockFile
    if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
        throw "Locked restore input is missing: $relativeLockFile"
    }
}

$expectedRuntimeLockFiles = @(
    "src\EmbyPlayer.App\packages.win-x64.lock.json",
    "src\EmbyPlayer.Core\packages.win-x64.lock.json",
    "src\EmbyPlayer.Emby\packages.win-x64.lock.json",
    "src\EmbyPlayer.Player\packages.win-x64.lock.json",
    "src\EmbyPlayer.UI\packages.win-x64.lock.json"
)
foreach ($relativeLockFile in $expectedRuntimeLockFiles) {
    $lockFile = Join-Path $workspaceRoot $relativeLockFile
    if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
        throw "Locked win-x64 restore input is missing: $relativeLockFile"
    }
}

if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    $outputRootFull = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $OutputRoot))
}

if (-not (Test-IsInsideWorkspace $outputRootFull)) {
    throw "OutputRoot must be a strict child of the repository workspace."
}

Assert-NoReparsePointInWorkspacePath $outputRootFull
if (Test-Path -LiteralPath $outputRootFull -PathType Leaf) {
    throw "OutputRoot points to a file instead of a directory."
}

$targetName = "$Version-$($Deployment.ToLowerInvariant())-$($Channel.ToLowerInvariant())"
if ($Channel -eq "Public") {
    $targetName = "JebyPlayer-$Version-win-x64-$($Deployment.ToLowerInvariant())"
}
$targetRoot = Join-Path $outputRootFull $targetName
$zipPath = "$targetRoot.zip"
if ((Test-Path -LiteralPath $targetRoot) -or (Test-Path -LiteralPath $zipPath)) {
    throw "Release target already exists; choose a new Version or OutputRoot: $targetRoot"
}

Assert-NoReparsePointInWorkspacePath $targetRoot

if ($PreflightOnly) {
    Write-Output "Release preflight passed without creating staging output."
    return
}

$transactionId = [Guid]::NewGuid().ToString("N")
$workingRoot = Join-Path $outputRootFull ".working-$transactionId"
$temporaryZipPath = Join-Path $outputRootFull ".working-$transactionId.zip"
if ((Test-Path -LiteralPath $workingRoot) -or (Test-Path -LiteralPath $temporaryZipPath)) {
    throw "Unique release transaction path already exists."
}

$finalDirectoryMoved = $false
$finalZipMoved = $false
try {
[void] [System.IO.Directory]::CreateDirectory($outputRootFull)
Assert-NoReparsePointInWorkspacePath $outputRootFull
[void] [System.IO.Directory]::CreateDirectory($workingRoot)
Assert-NoReparsePointInWorkspacePath $workingRoot
$artifactsPath = Join-Path $workingRoot "artifacts"
$publishDirectory = Join-Path $workingRoot "publish"
$testTempPath = Join-Path $workingRoot "test-temp"
[void] [System.IO.Directory]::CreateDirectory($artifactsPath)
[void] [System.IO.Directory]::CreateDirectory($publishDirectory)

$dotnetSdkVersion = ((& dotnet --version) -join "").Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetSdkVersion)) {
    throw "Unable to determine the selected .NET SDK version."
}

$restoreArguments = @(
    "restore",
    "EmbyPlayer.sln",
    "--locked-mode",
    "--disable-parallel",
    "--artifacts-path",
    $artifactsPath
)
Invoke-DotNet -Arguments $restoreArguments -Operation "locked solution restore"

$testsSkipped = $SkipTests.IsPresent
if (-not $testsSkipped) {
    [void] [System.IO.Directory]::CreateDirectory($testTempPath)
    $originalTemp = $env:TEMP
    $originalTmp = $env:TMP
    try {
        $env:TEMP = $testTempPath
        $env:TMP = $testTempPath
        & $buildTestScript -Configuration Release -ArtifactsPath $artifactsPath
        if (-not $?) {
            throw "Release build or tests failed."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable("TEMP", $originalTemp, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable("TMP", $originalTmp, [EnvironmentVariableTarget]::Process)
    }
}

$selfContained = $Deployment -eq "SelfContained"
$selfContainedText = $selfContained.ToString().ToLowerInvariant()
$runtimeRestoreArguments = @(
    "restore",
    $appProject,
    "--locked-mode",
    "--disable-parallel",
    "--artifacts-path",
    $artifactsPath,
    "-p:RuntimeIdentifier=win-x64",
    "-p:SelfContained=$selfContainedText"
)
Invoke-DotNet -Arguments $runtimeRestoreArguments -Operation "locked win-x64 application restore"

$publishArguments = @(
    "publish",
    $appProject,
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    $selfContainedText,
    "--no-restore",
    "--artifacts-path",
    $artifactsPath,
    "-m:1",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:Version=$Version",
    "-o",
    $publishDirectory
)
Invoke-DotNet -Arguments $publishArguments -Operation "win-x64 publish"

Assert-NoReparsePointTree -Root $publishDirectory
Copy-MpvLicenseEvidence -LicenseEvidence $mpvLicenseEvidence -PublishDirectory $publishDirectory
$dotnetLicenseEvidence = @()
if ($selfContained) {
    $nugetCacheOutput = @(& dotnet nuget locals global-packages --list --force-english-output)
    if ($LASTEXITCODE -ne 0 -or $nugetCacheOutput.Count -ne 1 -or
        $nugetCacheOutput[0] -notmatch '^global-packages:\s*(.+)$') {
        throw "Unable to resolve the configured NuGet global-packages cache for .NET license evidence."
    }
    $globalPackagesRoot = $Matches[1].Trim()
    $dotnetLicenseEvidence = @(Copy-DotNetRuntimeLicenseEvidence `
        -RuntimeConfigPath (Join-Path $publishDirectory "JebyPlayer.runtimeconfig.json") `
        -GlobalPackagesRoot $globalPackagesRoot `
        -PublishDirectory $publishDirectory)
}
$publishedFileItems = @(Get-ReleaseRegularFiles -Root $publishDirectory)
$publishedPdbFiles = @($publishedFileItems | Where-Object { $_.Extension.Equals(".pdb", [StringComparison]::OrdinalIgnoreCase) })
if ($publishedPdbFiles.Count -gt 0) {
    throw "Published payload must not contain PDB files."
}

$requiredPayloadFiles = @(
    "JebyPlayer.exe",
    "JebyPlayer.dll",
    "EmbyPlayer.Core.dll",
    "EmbyPlayer.Emby.dll",
    "EmbyPlayer.Player.dll",
    "EmbyPlayer.UI.dll",
    "libmpv-2.dll",
    "mpv-runtime.json"
)
foreach ($requiredPayloadFile in $requiredPayloadFiles) {
    $requiredPath = Join-Path $publishDirectory $requiredPayloadFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Published payload is missing required file: $requiredPayloadFile"
    }
}

$publishedMpvRuntime = Assert-MpvRuntime -binaryPath (Join-Path $publishDirectory "libmpv-2.dll") -manifest $mpvManifest
$publishedMpvDependencies = @(Assert-MpvRuntimeDependencies -RuntimeDirectory $publishDirectory -Manifest $mpvManifest)
$publishedMpvManifestHash = Get-FileSha256 -Path (Join-Path $publishDirectory "mpv-runtime.json")
$sourceMpvManifestHash = Get-FileSha256 -Path $mpvManifestPath
if (-not $publishedMpvManifestHash.Equals($sourceMpvManifestHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Published MPV runtime manifest does not match the tracked input."
}

foreach ($license in $mpvLicenseEvidence) {
    $publishedLicensePath = Join-Path $publishDirectory (([string] $license.packagePath).Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $publishedLicensePath -PathType Leaf)) {
        throw "Published payload is missing MPV license evidence: $($license.packagePath)"
    }

    $publishedLicenseHash = Get-FileSha256 -Path $publishedLicensePath
    if (-not $publishedLicenseHash.Equals([string] $license.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published MPV license evidence hash changed: $($license.packagePath)"
    }
}

& $sensitiveScript -PayloadRoot $publishDirectory
if (-not $?) {
    throw "Published payload sensitive-file validation failed."
}

$launchSmoke = if ($SkipLaunchSmoke) {
    [ordered]@{
        status = "skipped"
        processId = $null
        visibleTopLevelWindow = $false
    }
}
else {
    Invoke-LaunchSmoke `
        -executablePath (Join-Path $publishDirectory "JebyPlayer.exe") `
        -AllowForceTermination (-not $SafeLaunchSmoke)
}

$publishPrefix = $publishDirectory.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$payloadFiles = @(
    Get-ReleaseRegularFiles -Root $publishDirectory |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($publishPrefix.Length).Replace('\', '/')
            [ordered]@{
                relativePath = $relativePath
                sizeBytes = $_.Length
                sha256 = Get-FileSha256 -Path $_.FullName
            }
        }
)
$payloadFiles = @(Sort-ReleaseManifestEntriesOrdinal -Entries $payloadFiles)

$releaseManifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    channel = $Channel
    deployment = $Deployment
    runtimeIdentifier = "win-x64"
    selfContained = $selfContained
    distributionReady = ($Channel -eq "Public" -and $mpvPublicReady)
    testsSkipped = $testsSkipped
    createdAtUtc = [DateTime]::UtcNow.ToString("o")
    git = [ordered]@{
        branch = $branch
        commit = $commit
        workingTreeDirty = $workingTreeDirty
    }
    dotnetSdkVersion = $dotnetSdkVersion
    runtimeRequirement = if ($selfContained) { $null } else { ".NET 8 Desktop Runtime (x64)" }
    dotnetLicenseEvidence = $dotnetLicenseEvidence
    mpv = [ordered]@{
        provenanceStatus = [string] $mpvManifest.provenanceStatus
        sourceUrl = $mpvManifest.sourceUrl
        buildRecipeClue = [string] $mpvManifest.buildRecipeClue
        buildRecipeRevision = $mpvManifest.buildRecipeRevision
        embeddedRevisionLabel = [string] $mpvManifest.embeddedRevisionLabel
        licenseExpression = $mpvManifest.license.expression
        localArchiveFileName = [string] $mpvManifest.localArchive.fileName
        localArchiveSha256 = [string] $mpvManifest.localArchive.sha256
        binary = $publishedMpvRuntime
        dependencies = $publishedMpvDependencies
        licenseEvidence = @(
            $mpvLicenseEvidence |
                ForEach-Object {
                    [ordered]@{
                        sourceRelativePath = [string] $_.sourceRelativePath
                        packagePath = [string] $_.packagePath
                        sizeBytes = [long] $_.sizeBytes
                        sha256 = [string] $_.sha256
                    }
                }
        )
    }
    launchSmoke = $launchSmoke
    fileHashScope = "publish payload; release-manifest.json excluded to avoid a self-hash"
    files = $payloadFiles
}

$releaseManifestPath = Join-Path $publishDirectory "release-manifest.json"
$releaseJson = $releaseManifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText(
    $releaseManifestPath,
    $releaseJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

& $sensitiveScript -PayloadRoot $publishDirectory
if (-not $?) {
    throw "Final release text payload sensitive-file validation failed."
}

Assert-NoReparsePointTree -Root $publishDirectory
Remove-OwnedWorkingChildDirectory -Path $artifactsPath -WorkingRoot $workingRoot
Remove-OwnedWorkingChildDirectory -Path $testTempPath -WorkingRoot $workingRoot

if (-not $DirectoryOnly) {
    $zipInputs = @(Get-ChildItem -LiteralPath $publishDirectory -Force | ForEach-Object { $_.FullName })
    if ($zipInputs.Count -eq 0) {
        throw "Published payload is unexpectedly empty."
    }

    Compress-Archive -LiteralPath $zipInputs -DestinationPath $temporaryZipPath -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $temporaryZipPath -PathType Leaf) -or (Get-Item -LiteralPath $temporaryZipPath).Length -le 0) {
        throw "Release ZIP was not created successfully."
    }
}

Move-Item -LiteralPath $publishDirectory -Destination $targetRoot -ErrorAction Stop
$finalDirectoryMoved = $true
try {
    if (-not $DirectoryOnly) {
        Move-Item -LiteralPath $temporaryZipPath -Destination $zipPath -ErrorAction Stop
        $finalZipMoved = $true
    }

    Remove-OwnedWorkingDirectory -Path $workingRoot -ExpectedPath $workingRoot -OutputRoot $outputRootFull
}
catch {
    $placementFailure = $_
    try {
        if ($finalZipMoved) {
            Move-Item -LiteralPath $zipPath -Destination $temporaryZipPath -ErrorAction Stop
            $finalZipMoved = $false
        }

        Move-Item -LiteralPath $targetRoot -Destination $publishDirectory -ErrorAction Stop
        $finalDirectoryMoved = $false
    }
    catch {
        $failedMarker = Join-Path $targetRoot "RELEASE-FAILED.txt"
        try {
            [System.IO.File]::WriteAllText(
                $failedMarker,
                "Final release placement failed and directory rollback also failed. Do not distribute this directory.`r`n",
                [System.Text.UTF8Encoding]::new($false))
        }
        catch {
            Write-Warning "Unable to write RELEASE-FAILED.txt after final release placement rollback failed."
        }

        throw "Final release placement failed and could not be rolled back. A failed release may remain at $targetRoot"
    }

    throw $placementFailure
}

$releaseManifestPath = Join-Path $targetRoot "release-manifest.json"
Write-Output "Release staging: $targetRoot"
Write-Output "Release manifest: $releaseManifestPath"
if (-not $DirectoryOnly) {
    Write-Output "Release ZIP: $zipPath"
}
}
catch {
    $releaseFailure = $_
    $cleanupFailures = @()
    if (-not $finalDirectoryMoved) {
        try {
            Remove-OwnedWorkingDirectory -Path $workingRoot -ExpectedPath $workingRoot -OutputRoot $outputRootFull
        }
        catch {
            $cleanupFailures += $_.Exception.Message
        }
    }

    try {
        Remove-OwnedTemporaryZip -Path $temporaryZipPath -ExpectedPath $temporaryZipPath -OutputRoot $outputRootFull
    }
    catch {
        $cleanupFailures += $_.Exception.Message
    }

    if ($cleanupFailures.Count -gt 0) {
        throw ("Release failed: $($releaseFailure.Exception.Message) Cleanup also failed: " + ($cleanupFailures -join " | "))
    }

    throw $releaseFailure
}
