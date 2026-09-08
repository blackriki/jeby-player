Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$dailyValidationScript = Join-Path $PSScriptRoot "release-validation.ps1"
. $dailyValidationScript

function Assert-DailyNoReparsePointAncestors([string] $Path) {
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($pathFull)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) {
        throw "Daily deployment path has no filesystem root: $pathFull"
    }

    $current = [System.IO.DirectoryInfo]::new($pathFull)
    while ($null -ne $current) {
        if ($current.Exists) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (-not $item.PSIsContainer) {
                throw "Daily deployment path expected a directory: $($current.FullName)"
            }

            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Daily deployment paths must not traverse a reparse point: $($current.FullName)"
            }
        }

        if ($current.FullName.TrimEnd('\', '/').Equals(
                $pathRoot.TrimEnd('\', '/'),
                [StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        $current = $current.Parent
    }

    throw "Daily deployment path did not resolve beneath its filesystem root: $pathFull"
}

function Assert-DailyPlayerNotRunning {
    $runningPlayers = @(Get-Process -Name "EmbyPlayer.App" -ErrorAction SilentlyContinue)
    if ($runningPlayers.Count -gt 0) {
        $processIds = @($runningPlayers | ForEach-Object { $_.Id }) -join ", "
        throw "Jeby Player is running (PID: $processIds). Close it and retry; daily deployment never stops the app."
    }
}

function Open-DailyDeploymentLock([string] $InstallRoot) {
    $installFull = [System.IO.Path]::GetFullPath($InstallRoot)
    $pathRoot = [System.IO.Path]::GetPathRoot($installFull)
    if ([string]::IsNullOrWhiteSpace($pathRoot) -or
        $installFull.TrimEnd('\', '/').Equals(
            $pathRoot.TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Daily InstallRoot must not be a filesystem root."
    }

    Assert-DailyNoReparsePointAncestors -Path $installFull
    if (Test-Path -LiteralPath $installFull -PathType Leaf) {
        throw "Daily InstallRoot points to a file: $installFull"
    }

    [void] [System.IO.Directory]::CreateDirectory($installFull)
    Assert-NoReparsePointInControlledPath `
        -Root $pathRoot `
        -Path $installFull `
        -Description "Daily InstallRoot"

    $lockPath = Join-Path $installFull ".daily-deploy.lock"
    if (Test-Path -LiteralPath $lockPath) {
        $lockItem = Get-Item -LiteralPath $lockPath -Force
        if ($lockItem.PSIsContainer -or
            ($lockItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Daily deployment lock must be a normal file: $lockPath"
        }
    }

    try {
        $lockStream = [System.IO.FileStream]::new(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        throw "Another daily deployment is already using this InstallRoot. Wait for it to finish: $installFull"
    }

    try {
        $lockItem = Get-Item -LiteralPath $lockPath -Force
        if ($lockItem.PSIsContainer -or
            ($lockItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Daily deployment lock must be a normal file: $lockPath"
        }

        return $lockStream
    }
    catch {
        $lockStream.Dispose()
        throw
    }
}

function Invoke-DailyReleasePublish(
    [string] $PublishScript,
    [string] $Version,
    [string] $OutputRoot) {
    & $PublishScript `
        -Version $Version `
        -Deployment SelfContained `
        -Channel Internal `
        -OutputRoot $OutputRoot `
        -AllowDirty `
        -DirectoryOnly `
        -SafeLaunchSmoke
    if (-not $?) {
        throw "Daily SelfContained Internal publish failed."
    }
}

function Assert-DailyPayload(
    [string] $PayloadRoot,
    [string] $ExpectedCommit) {
    $payloadFull = [System.IO.Path]::GetFullPath($PayloadRoot)
    if (-not (Test-Path -LiteralPath $payloadFull -PathType Container)) {
        throw "Daily deployment payload is missing: $payloadFull"
    }

    Assert-NoReparsePointTree -Root $payloadFull
    $payloadFiles = @(Get-ReleaseRegularFiles -Root $payloadFull)
    $pdbFiles = @($payloadFiles | Where-Object { $_.Extension.Equals(".pdb", [StringComparison]::OrdinalIgnoreCase) })
    if ($pdbFiles.Count -gt 0) {
        throw "Daily deployment payload must not contain PDB files: $($pdbFiles[0].FullName)"
    }

    $requiredFiles = @(
        "EmbyPlayer.App.exe",
        "EmbyPlayer.App.dll",
        "EmbyPlayer.Core.dll",
        "EmbyPlayer.Emby.dll",
        "EmbyPlayer.Player.dll",
        "EmbyPlayer.UI.dll",
        "libmpv-2.dll",
        "mpv-runtime.json",
        "release-manifest.json"
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadFull $requiredFile) -PathType Leaf)) {
            throw "Daily deployment payload is missing required file: $requiredFile"
        }
    }

    if (Test-Path -LiteralPath (Join-Path $payloadFull "RELEASE-FAILED.txt")) {
        throw "Daily deployment refuses a payload marked RELEASE-FAILED.txt."
    }

    $manifestPath = Join-Path $payloadFull "release-manifest.json"
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Daily deployment manifest is invalid JSON: $($_.Exception.Message)"
    }

    if ([int] $manifest.schemaVersion -ne 1 -or
        -not ([string] $manifest.channel).Equals("Internal", [StringComparison]::Ordinal) -or
        -not ([string] $manifest.deployment).Equals("SelfContained", [StringComparison]::Ordinal) -or
        -not [bool] $manifest.selfContained -or
        [bool] $manifest.distributionReady -or
        [bool] $manifest.testsSkipped) {
        throw "Daily deployment requires a tested SelfContained Internal manifest."
    }

    if (-not ([string] $manifest.git.commit).Equals($ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Daily deployment manifest commit does not match the requested source commit."
    }

    if (-not ([string] $manifest.launchSmoke.status).Equals("passed", [StringComparison]::Ordinal) -or
        -not [bool] $manifest.launchSmoke.visibleTopLevelWindow) {
        throw "Daily deployment requires a passed visible-window launch smoke."
    }

    $payloadPrefix = $payloadFull.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $diskFilesByRelativePath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($file in $payloadFiles) {
        if ($file.Name.Equals("release-manifest.json", [StringComparison]::Ordinal)) {
            continue
        }

        $relativePath = $file.FullName.Substring($payloadPrefix.Length).Replace('\', '/')
        $diskFilesByRelativePath.Add($relativePath, $file)
    }

    $manifestEntries = @($manifest.files)
    $manifestPaths = [string[]] @($manifestEntries | ForEach-Object { [string] $_.relativePath })
    $sortedManifestPaths = [string[]] $manifestPaths.Clone()
    [Array]::Sort($sortedManifestPaths, [StringComparer]::Ordinal)
    for ($index = 0; $index -lt $manifestPaths.Count; $index++) {
        if (-not $manifestPaths[$index].Equals($sortedManifestPaths[$index], [StringComparison]::Ordinal)) {
            throw "Daily deployment manifest files must use ordinal relative-path order."
        }
    }

    if ($manifestEntries.Count -ne $diskFilesByRelativePath.Count) {
        throw "Daily deployment manifest file count does not match the payload."
    }

    $seenManifestPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifestEntries) {
        $relativePath = [string] $entry.relativePath
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            -not $seenManifestPaths.Add($relativePath) -or
            -not $diskFilesByRelativePath.ContainsKey($relativePath)) {
            throw "Daily deployment manifest contains an unsafe, duplicate, or missing path: $relativePath"
        }

        $file = $diskFilesByRelativePath[$relativePath]
        if ([long] $entry.sizeBytes -ne $file.Length) {
            throw "Daily deployment manifest size mismatch: $relativePath"
        }

        $actualHash = Get-FileSha256 -Path $file.FullName
        if (-not $actualHash.Equals([string] $entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Daily deployment manifest hash mismatch: $relativePath"
        }
    }

    return $manifest
}

function Remove-DailyOwnedDirectory(
    [string] $Path,
    [string] $ControlledRoot,
    [string[]] $AllowedPrefixes) {
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $controlledRootFull = [System.IO.Path]::GetFullPath($ControlledRoot)
    $parent = [System.IO.Directory]::GetParent($pathFull)
    $name = [System.IO.Path]::GetFileName($pathFull)
    $ownedName = $false
    foreach ($prefix in $AllowedPrefixes) {
        if ($name.StartsWith($prefix, [StringComparison]::Ordinal)) {
            $ownedName = $true
            break
        }
    }

    if ($null -eq $parent -or
        -not $parent.FullName.Equals($controlledRootFull, [StringComparison]::OrdinalIgnoreCase) -or
        -not $ownedName) {
        throw "Refusing to remove an unowned daily deployment directory: $pathFull"
    }

    if (-not (Test-Path -LiteralPath $pathFull)) {
        return
    }

    $item = Get-Item -LiteralPath $pathFull -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove an unsafe daily deployment directory: $pathFull"
    }

    Assert-NoReparsePointTree -Root $pathFull
    Remove-Item -LiteralPath $pathFull -Recurse -Force -ErrorAction Stop
}

function Copy-DailyPayload(
    [string] $PayloadRoot,
    [string] $DestinationRoot) {
    $payloadFull = [System.IO.Path]::GetFullPath($PayloadRoot)
    $destinationFull = [System.IO.Path]::GetFullPath($DestinationRoot)
    [void] [System.IO.Directory]::CreateDirectory($destinationFull)
    $payloadPrefix = $payloadFull.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ReleaseRegularFiles -Root $payloadFull) {
        $relativePath = $file.FullName.Substring($payloadPrefix.Length)
        $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $destinationFull $relativePath))
        if (-not (Test-ReleasePathInside -Root $destinationFull -Path $destinationPath)) {
            throw "Daily deployment copy escaped its destination: $destinationPath"
        }

        $destinationParent = Split-Path -Parent $destinationPath
        [void] [System.IO.Directory]::CreateDirectory($destinationParent)
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force -ErrorAction Stop
    }
}

function Invoke-DailyPayloadPromotion(
    [string] $PayloadRoot,
    [string] $InstallRoot,
    [string] $ExpectedCommit,
    [scriptblock] $MoveDirectory = {
        param([string] $Source, [string] $Destination)
        Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop
    },
    [scriptblock] $RemoveDirectory = {
        param([string] $Path, [string] $ControlledRoot, [string[]] $AllowedPrefixes)
        Remove-DailyOwnedDirectory `
            -Path $Path `
            -ControlledRoot $ControlledRoot `
            -AllowedPrefixes $AllowedPrefixes
    }) {
    $payloadFull = [System.IO.Path]::GetFullPath($PayloadRoot)
    $installFull = [System.IO.Path]::GetFullPath($InstallRoot)
    $installPathRoot = [System.IO.Path]::GetPathRoot($installFull)
    if ($installFull.TrimEnd('\', '/').Equals(
            $installPathRoot.TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Daily InstallRoot must not be a filesystem root."
    }

    if ((Test-ReleasePathInsideOrEqual -Root $payloadFull -Path $installFull) -or
        (Test-ReleasePathInsideOrEqual -Root $installFull -Path $payloadFull)) {
        throw "Daily payload and InstallRoot must not contain one another."
    }

    Assert-DailyNoReparsePointAncestors -Path $installFull
    if (Test-Path -LiteralPath $installFull -PathType Leaf) {
        throw "Daily InstallRoot points to a file: $installFull"
    }

    [void] [System.IO.Directory]::CreateDirectory($installFull)
    Assert-NoReparsePointInControlledPath `
        -Root $installPathRoot `
        -Path $installFull `
        -Description "Daily InstallRoot"
    $null = Assert-DailyPayload -PayloadRoot $payloadFull -ExpectedCommit $ExpectedCommit
    Assert-DailyPlayerNotRunning

    $currentPath = Join-Path $installFull "current"
    $previousPath = Join-Path $installFull "previous"
    foreach ($stablePath in @($currentPath, $previousPath)) {
        if (Test-Path -LiteralPath $stablePath) {
            $stableItem = Get-Item -LiteralPath $stablePath -Force
            if (-not $stableItem.PSIsContainer -or
                ($stableItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Daily deployment stable path must be a normal directory: $stablePath"
            }

            Assert-NoReparsePointTree -Root $stablePath
        }
    }

    $transactionPrefixes = @(".next-", ".rollback-", ".previous-old-")
    $staleTransactions = @(
        Get-ChildItem -LiteralPath $installFull -Force |
            Where-Object {
                $entryName = $_.Name
                @($transactionPrefixes | Where-Object {
                    $entryName.StartsWith($_, [StringComparison]::Ordinal)
                }).Count -gt 0
            }
    )
    if ($staleTransactions.Count -gt 0) {
        throw "Daily InstallRoot contains an interrupted transaction; inspect it before retrying: $($staleTransactions[0].FullName)"
    }

    $hadCurrent = Test-Path -LiteralPath $currentPath -PathType Container
    $hadPrevious = Test-Path -LiteralPath $previousPath -PathType Container
    if (-not $hadCurrent -and $hadPrevious) {
        throw "Daily InstallRoot has previous without current; refusing an ambiguous rotation."
    }

    $transactionId = [Guid]::NewGuid().ToString("N")
    $nextPath = Join-Path $installFull ".next-$transactionId"
    $rollbackPath = Join-Path $installFull ".rollback-$transactionId"
    $oldPreviousPath = Join-Path $installFull ".previous-old-$transactionId"
    $rotationStarted = $false
    $promotionCommitted = $false
    $rollbackSucceeded = $false
    $currentBackedUp = $false
    $nextInstalled = $false
    $previousBackedUp = $false
    $oldCurrentMovedToPrevious = $false
    $operationFailure = $null
    $rollbackFailures = @()

    try {
        Copy-DailyPayload -PayloadRoot $payloadFull -DestinationRoot $nextPath
        $null = Assert-DailyPayload -PayloadRoot $nextPath -ExpectedCommit $ExpectedCommit
        Assert-DailyPlayerNotRunning

        $rotationStarted = $true
        if ($hadCurrent) {
            & $MoveDirectory $currentPath $rollbackPath
            $currentBackedUp = $true
        }

        & $MoveDirectory $nextPath $currentPath
        $nextInstalled = $true

        if ($hadCurrent) {
            if ($hadPrevious) {
                & $MoveDirectory $previousPath $oldPreviousPath
                $previousBackedUp = $true
            }

            & $MoveDirectory $rollbackPath $previousPath
            $oldCurrentMovedToPrevious = $true
            $currentBackedUp = $false
        }

        $promotionCommitted = $true
    }
    catch {
        $operationFailure = $_
        try {
            if ($hadCurrent) {
                if ($nextInstalled -and (Test-Path -LiteralPath $currentPath)) {
                    & $MoveDirectory $currentPath $nextPath
                    $nextInstalled = $false
                }

                if ($oldCurrentMovedToPrevious) {
                    & $MoveDirectory $previousPath $currentPath
                    $oldCurrentMovedToPrevious = $false
                }
                elseif ($currentBackedUp) {
                    & $MoveDirectory $rollbackPath $currentPath
                    $currentBackedUp = $false
                }

                if ($previousBackedUp) {
                    & $MoveDirectory $oldPreviousPath $previousPath
                    $previousBackedUp = $false
                }
            }
            elseif ($nextInstalled -and (Test-Path -LiteralPath $currentPath)) {
                & $MoveDirectory $currentPath $nextPath
                $nextInstalled = $false
            }

            $rollbackSucceeded = $true
        }
        catch {
            $rollbackFailures += $_.Exception.Message
        }
    }
    finally {
        if ($promotionCommitted -or $rollbackSucceeded -or -not $rotationStarted) {
            $temporaryPaths = @($nextPath, $rollbackPath)
            if (-not $promotionCommitted) {
                $temporaryPaths += $oldPreviousPath
            }

            foreach ($temporaryPath in $temporaryPaths) {
                if (Test-Path -LiteralPath $temporaryPath) {
                    try {
                        & $RemoveDirectory $temporaryPath $installFull $transactionPrefixes
                    }
                    catch {
                        $rollbackFailures += $_.Exception.Message
                    }
                }
            }
        }
    }

    if ($null -ne $operationFailure) {
        if ($rollbackFailures.Count -gt 0) {
            throw ("Daily deployment failed: $($operationFailure.Exception.Message) " +
                "Best-effort rollback or cleanup also failed: " + ($rollbackFailures -join " | "))
        }

        throw $operationFailure
    }

    if ($rollbackFailures.Count -gt 0) {
        throw ("Daily deployment completed but transaction cleanup failed: " + ($rollbackFailures -join " | "))
    }

    if ($promotionCommitted -and $previousBackedUp) {
        try {
            & $RemoveDirectory $oldPreviousPath $installFull @(".previous-old-")
            $previousBackedUp = $false
        }
        catch {
            throw ("Daily deployment committed a healthy current and previous, but cleanup of the oldest previous copy failed. " +
                "Inspect the retained transaction evidence at $oldPreviousPath. $($_.Exception.Message)")
        }
    }

    return [pscustomobject]@{
        CurrentPath = $currentPath
        PreviousPath = if ($hadCurrent) { $previousPath } else { $null }
    }
}

function Invoke-DailyBuildTransaction(
    [string] $WorkspaceRoot,
    [scriptblock] $Operation) {
    $workspaceFull = [System.IO.Path]::GetFullPath($WorkspaceRoot)
    $dailyBase = Join-Path $workspaceFull ".tmp\daily-deploy"
    Assert-DailyNoReparsePointAncestors -Path $dailyBase
    [void] [System.IO.Directory]::CreateDirectory($dailyBase)
    Assert-NoReparsePointInControlledPath `
        -Root $workspaceFull `
        -Path $dailyBase `
        -Description "Daily build root"

    $transactionRoot = Join-Path $dailyBase (".daily-" + [Guid]::NewGuid().ToString("N"))
    [void] [System.IO.Directory]::CreateDirectory($transactionRoot)
    $operationResult = $null
    $operationFailure = $null
    $cleanupFailure = $null
    try {
        $operationResult = @(& $Operation $transactionRoot | ForEach-Object {
            # Keep diagnostics visible even when a later exception prevents array assignment.
            if ($_ -is [string]) {
                Write-Host $_
            }
            $_
        })
    }
    catch {
        $operationFailure = $_
    }
    finally {
        try {
            Remove-DailyOwnedDirectory `
                -Path $transactionRoot `
                -ControlledRoot $dailyBase `
                -AllowedPrefixes @(".daily-")
        }
        catch {
            $cleanupFailure = $_
        }
    }

    if ($null -ne $operationFailure) {
        if ($null -ne $cleanupFailure) {
            throw "Daily build failed: $($operationFailure.Exception.Message) Cleanup also failed: $($cleanupFailure.Exception.Message)"
        }

        throw $operationFailure
    }

    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }

    return $operationResult
}
