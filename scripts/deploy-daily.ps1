[CmdletBinding()]
param(
    [string] $InstallRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$workspaceRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$supportScript = Join-Path $PSScriptRoot "daily-deploy-support.ps1"
. $supportScript

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw "Unable to resolve LocalApplicationData for the default daily InstallRoot."
    }

    $installRootFull = [System.IO.Path]::GetFullPath((Join-Path $localAppData "Programs\JebyPlayer"))
}
elseif ([System.IO.Path]::IsPathRooted($InstallRoot)) {
    $installRootFull = [System.IO.Path]::GetFullPath($InstallRoot)
}
else {
    $installRootFull = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $InstallRoot))
}

$deploymentLock = Open-DailyDeploymentLock -InstallRoot $installRootFull
try {
    Assert-DailyPlayerNotRunning
    $sourceCommit = ((& git -C $workspaceRoot rev-parse HEAD) -join "").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
        throw "Unable to resolve the current Git commit for daily deployment."
    }

    $dailyResult = Invoke-DailyBuildTransaction -WorkspaceRoot $workspaceRoot -Operation {
        param([string] $dailyOutputRoot)
        $version = "0.0.0-daily.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
        Invoke-DailyReleasePublish `
            -PublishScript $publishScript `
            -Version $version `
            -OutputRoot $dailyOutputRoot

        $payloadRoot = Join-Path $dailyOutputRoot "$version-selfcontained-internal"
        Invoke-DailyPayloadPromotion `
            -PayloadRoot $payloadRoot `
            -InstallRoot $installRootFull `
            -ExpectedCommit $sourceCommit
    }

    $promotionResult = @($dailyResult | Where-Object { $null -ne $_.PSObject.Properties["CurrentPath"] }) | Select-Object -Last 1
    if ($null -eq $promotionResult) {
        throw "Daily deployment completed without a promotion result."
    }

    Write-Output "Daily deployment current: $($promotionResult.CurrentPath)"
    if ($null -ne $promotionResult.PreviousPath) {
        Write-Output "Daily deployment previous: $($promotionResult.PreviousPath)"
    }
}
finally {
    $deploymentLock.Dispose()
}
