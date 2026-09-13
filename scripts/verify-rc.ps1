[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ArtifactDirectory,
    [string] $ResultsDirectory,
    [string] $SessionDataDirectory,
    [switch] $ArtifactOnly,
    [switch] $RunSourceTests,
    [switch] $RunWindowLifecycle,
    [switch] $RunUiSmoke,
    # Uses the existing local session; leaves one previously unwatched, non-Continue sample watched.
    [switch] $RunRealServer,
    # Uses the existing local session; short playback may leave progress on one new sample.
    [switch] $RunAudioSwitch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$workspaceRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
. (Join-Path $PSScriptRoot "daily-deploy-support.ps1")

function Resolve-RcPath([string] $Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $Path))
}

if ($ArtifactOnly -and ($RunSourceTests -or $RunWindowLifecycle -or $RunUiSmoke -or $RunRealServer -or $RunAudioSwitch)) {
    throw "ArtifactOnly cannot be combined with execution switches."
}

$sessionDataFull = $null
if ($PSBoundParameters.ContainsKey("SessionDataDirectory")) {
    if ([string]::IsNullOrWhiteSpace($SessionDataDirectory) -or $ArtifactOnly -or -not ($RunRealServer -or $RunAudioSwitch)) {
        throw "SessionDataDirectory requires a nonempty path and RunRealServer or RunAudioSwitch."
    }

    $sessionDataFull = Resolve-RcPath $SessionDataDirectory
    if (-not (Test-Path -LiteralPath $sessionDataFull -PathType Container)) {
        throw "SessionDataDirectory must be an existing directory."
    }

    Assert-DailyNoReparsePointAncestors -Path $sessionDataFull
}

$artifactFull = Resolve-RcPath $ArtifactDirectory
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = ".tmp\rc-acceptance\run-" + [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + [Guid]::NewGuid().ToString("N")
}

$resultsFull = Resolve-RcPath $ResultsDirectory
if (-not (Test-ReleasePathInside -Root (Join-Path $workspaceRoot ".tmp") -Path $resultsFull)) {
    throw "ResultsDirectory must be a new directory beneath workspace .tmp."
}

if ((Test-ReleasePathInsideOrEqual -Root $artifactFull -Path $resultsFull) -or
    (Test-ReleasePathInsideOrEqual -Root $resultsFull -Path $artifactFull)) {
    throw "ArtifactDirectory and ResultsDirectory must not overlap."
}

if ($null -ne $sessionDataFull -and
    ((Test-ReleasePathInsideOrEqual -Root $sessionDataFull -Path $resultsFull) -or
    (Test-ReleasePathInsideOrEqual -Root $resultsFull -Path $sessionDataFull))) {
    throw "SessionDataDirectory and ResultsDirectory must not overlap."
}

Assert-DailyNoReparsePointAncestors -Path $resultsFull
if (Test-Path -LiteralPath $resultsFull) {
    throw "ResultsDirectory already exists; choose a new directory to preserve earlier evidence."
}

[void] [System.IO.Directory]::CreateDirectory($resultsFull)
$steps = [System.Collections.Generic.List[object]]::new()
$startedAtUtc = [DateTime]::UtcNow.ToString("o")
$artifactManifest = $null
$manifestSha256 = $null
$currentSourceCommit = $null
$currentSourceDirty = $null
$runnerOutput = Join-Path $resultsFull "runner"
$runnerProject = Join-Path $workspaceRoot "tests\EmbyPlayer.Acceptance\EmbyPlayer.Acceptance.csproj"
$buildArguments = @("-m:1", "-nr:false", "-p:UseSharedCompilation=false")
$environmentNames = @("DOTNET_PROCESSOR_COUNT", "MSBUILDDISABLENODEREUSE", "TEMP", "TMP")
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
}

function Add-RcNotRun([string] $Name, [string] $Reason) {
    $steps.Add([ordered]@{ name = $Name; status = "NotRun"; reason = $Reason; evidencePaths = @() })
    Write-Output "RC-NOTRUN:$Name ($Reason)"
}

function Invoke-RcStep([string] $Name, [scriptblock] $Action, [string[]] $EvidencePaths = @()) {
    $logPath = Join-Path $resultsFull "$Name.log"
    $step = [ordered]@{
        name = $Name
        status = "Failed"
        startedAtUtc = [DateTime]::UtcNow.ToString("o")
        evidencePaths = @($logPath) + $EvidencePaths
    }
    try {
        & $Action $step *>&1 | Tee-Object -FilePath $logPath | Out-Host
        if ($step.status -ne "NotRun") { $step.status = "Passed" }
        Write-Host "RC-$($step.status.ToUpperInvariant()):$Name"
        return $step.status -eq "Passed"
    }
    catch {
        $step.errorType = $_.Exception.GetType().Name
        $step.reason = $_.Exception.Message
        Write-Warning "RC-FAILED:$Name ($($step.reason))"
        return $false
    }
    finally {
        $step.completedAtUtc = [DateTime]::UtcNow.ToString("o")
        $steps.Add($step)
    }
}

function Invoke-RcDotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet returned exit code $LASTEXITCODE."
    }
}

function Invoke-RcRunner([string] $Check, [System.Collections.IDictionary] $Step) {
    $evidenceDirectory = Join-Path $resultsFull $Check
    $runnerArguments = @(
        (Join-Path $runnerOutput "EmbyPlayer.UI.Tests.dll"),
        "--artifact-directory", $artifactFull,
        "--check", $Check,
        "--output-directory", $evidenceDirectory
    )
    if ($null -ne $sessionDataFull -and $Check -in @("server-playback", "audio-switch")) {
        $runnerArguments += @("--session-data-directory", $sessionDataFull)
    }

    & dotnet @runnerArguments
    $runnerExitCode = $LASTEXITCODE
    $result = Get-Content -LiteralPath (Join-Path $evidenceDirectory "result.json") -Raw | ConvertFrom-Json
    if ($result.check -ne $Check) {
        throw "Acceptance runner returned a result for a different check."
    }

    if ($runnerExitCode -eq 3 -and $result.status -eq "NotRun") {
        $Step.status = "NotRun"
        $Step.reason = $result.errorCode
        return
    }

    if ($runnerExitCode -ne 0 -or $result.status -ne "Passed") {
        throw "Acceptance runner did not pass the requested check (exit code $runnerExitCode)."
    }
}

$exitCode = 1
Push-Location $workspaceRoot
try {
    $env:DOTNET_PROCESSOR_COUNT = "2"
    $env:MSBUILDDISABLENODEREUSE = "1"
    $testTemp = Join-Path $resultsFull "temp"
    [void] [System.IO.Directory]::CreateDirectory($testTemp)
    $env:TEMP = $testTemp
    $env:TMP = $testTemp

    $artifactPassed = Invoke-RcStep "artifact" {
        $manifestPath = Join-Path $artifactFull "release-manifest.json"
        Assert-NoReparsePointInControlledPath -Root ([System.IO.Path]::GetPathRoot($manifestPath)) -Path $manifestPath
        $candidate = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $script:artifactManifest = Assert-DailyPayload -PayloadRoot $artifactFull -ExpectedCommit ([string] $candidate.git.commit)
        if ($script:artifactManifest.git.workingTreeDirty -ne $false) {
            throw "RC acceptance requires an artifact built from a clean working tree."
        }

        $script:manifestSha256 = Get-FileSha256 -Path $manifestPath
        $script:currentSourceCommit = ((& git -C $workspaceRoot rev-parse HEAD) -join "").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Unable to read the current source commit." }
        $sourceStatus = @(& git -C $workspaceRoot status --porcelain --untracked-files=normal)
        if ($LASTEXITCODE -ne 0) { throw "Unable to read the current working-tree status." }
        $script:currentSourceDirty = $sourceStatus.Count -gt 0
        Write-Output "Clean SelfContained Internal payload, complete manifest hashes, and recorded release smoke verified."
    }

    $canExecute = $artifactPassed -and -not $ArtifactOnly
    $runnerReady = $false
    if ($canExecute) {
        $runnerReady = Invoke-RcStep "runner-build" {
            $runnerArtifacts = Join-Path $resultsFull "runner-build"
            $referenceArgument = "-p:ArtifactDirectory=$artifactFull"
            Invoke-RcDotNet -Arguments (@("restore", $runnerProject, "--disable-parallel", "--artifacts-path", $runnerArtifacts, $referenceArgument) + $buildArguments)
            Invoke-RcDotNet -Arguments (@("build", $runnerProject, "--no-restore", "-c", "Release", "--artifacts-path", $runnerArtifacts, "-o", $runnerOutput, $referenceArgument) + $buildArguments)
        }
    }
    else {
        Add-RcNotRun "runner-build" "ArtifactOnly selected or artifact verification failed."
    }

    if ($runnerReady) {
        [void] (Invoke-RcStep "settings-export" { param($step) Invoke-RcRunner "settings-export" $step } @((Join-Path $resultsFull "settings-export\result.json")))
    }
    else {
        Add-RcNotRun "settings-export" "The acceptance runner was not built."
    }

    if ($canExecute -and $RunSourceTests) {
        [void] (Invoke-RcStep "source-tests" {
            $sourceArtifacts = Join-Path $resultsFull "source-build"
            $trxDirectory = Join-Path $resultsFull "trx"
            Invoke-RcDotNet -Arguments (@("restore", "EmbyPlayer.sln", "--locked-mode", "--disable-parallel", "--artifacts-path", $sourceArtifacts) + $buildArguments)
            & (Join-Path $PSScriptRoot "build-test.ps1") -Configuration Release -ArtifactsPath $sourceArtifacts -ResultsDirectory $trxDirectory
            if (-not $?) { throw "Source build or tests failed." }
        } @((Join-Path $resultsFull "trx")))
    }
    else {
        Add-RcNotRun "source-tests" "RunSourceTests was not selected, ArtifactOnly was selected, or artifact verification failed."
    }

    if ($canExecute -and $RunWindowLifecycle) {
        [void] (Invoke-RcStep "window-lifecycle" {
            & (Join-Path $PSScriptRoot "verify-window-lifecycle.ps1") -ExecutablePath (Join-Path $artifactFull "JebyPlayer.exe")
            if (-not $?) { throw "Window lifecycle verification failed." }
        })
    }
    else {
        Add-RcNotRun "window-lifecycle" "RunWindowLifecycle was not selected, ArtifactOnly was selected, or artifact verification failed."
    }

    if ($runnerReady -and $RunUiSmoke) {
        [void] (Invoke-RcStep "ui-smoke" { param($step) Invoke-RcRunner "ui-smoke" $step } @((Join-Path $resultsFull "ui-smoke\result.json")))
    }
    else {
        Add-RcNotRun "ui-smoke" "RunUiSmoke was not selected or the acceptance runner was not built."
    }

    if ($runnerReady -and $RunRealServer) {
        [void] (Invoke-RcStep "server-playback" { param($step) Invoke-RcRunner "server-playback" $step } @((Join-Path $resultsFull "server-playback\result.json")))
    }
    else {
        Add-RcNotRun "server-playback" "RunRealServer was not selected or the acceptance runner was not built."
    }

    if ($runnerReady -and $RunAudioSwitch) {
        [void] (Invoke-RcStep "audio-switch" { param($step) Invoke-RcRunner "audio-switch" $step } @((Join-Path $resultsFull "audio-switch\result.json")))
    }
    else {
        Add-RcNotRun "audio-switch" "RunAudioSwitch was not selected or the acceptance runner was not built."
    }
}
catch {
    $steps.Add([ordered]@{ name = "orchestration"; status = "Failed"; reason = $_.Exception.Message; evidencePaths = @() })
}
finally {
    foreach ($name in $environmentNames) {
        $originalValue = if ($null -eq $savedEnvironment[$name]) { [NullString]::Value } else { $savedEnvironment[$name] }
        [Environment]::SetEnvironmentVariable($name, $originalValue, [EnvironmentVariableTarget]::Process)
    }
    Pop-Location
    $status = if (@($steps | Where-Object { $_.status -eq "Failed" }).Count -gt 0) { "Failed" }
        elseif (@($steps | Where-Object { $_.status -eq "NotRun" }).Count -gt 0) { "Incomplete" }
        else { "Passed" }
    $exitCode = switch ($status) { "Passed" { 0 }; "Incomplete" { 2 }; default { 1 } }
    $report = [ordered]@{
        schemaVersion = 1
        status = $status
        startedAtUtc = $startedAtUtc
        completedAtUtc = [DateTime]::UtcNow.ToString("o")
        artifactDirectory = $artifactFull
        artifactVersion = if ($null -ne $artifactManifest) { $artifactManifest.version } else { $null }
        artifactCommit = if ($null -ne $artifactManifest) { $artifactManifest.git.commit } else { $null }
        artifactManifestSha256 = $manifestSha256
        currentSourceCommit = $currentSourceCommit
        currentSourceWorkingTreeDirty = $currentSourceDirty
        sourceTestsScope = "Current source tree; not proof of behavior of the published artifact."
        trxPaths = @(Get-ChildItem -LiteralPath $resultsFull -Filter "*.trx" -Recurse -File | ForEach-Object { $_.FullName })
        steps = @($steps.ToArray())
    }
    $reportPath = Join-Path $resultsFull "summary.json"
    [System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 12), [System.Text.UTF8Encoding]::new($false))
    Write-Output "RC-$($status.ToUpperInvariant()): $reportPath"
}

exit $exitCode
