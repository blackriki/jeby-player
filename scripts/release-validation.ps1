function Test-ReleasePathInside([string] $Root, [string] $Path) {
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $rootPrefix = $rootFull.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    return $pathFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Test-ReleasePathInsideOrEqual([string] $Root, [string] $Path) {
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    return $pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-ReleasePathInside -Root $rootFull -Path $pathFull)
}

function Assert-NoReparsePointInControlledPath(
    [string] $Root,
    [string] $Path,
    [string] $Description = "Release path") {
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-ReleasePathInsideOrEqual -Root $rootFull -Path $pathFull)) {
        throw "$Description escaped its controlled root: $pathFull"
    }

    $currentPath = $pathFull
    while ($true) {
        if (-not (Test-Path -LiteralPath $currentPath)) {
            throw "$Description is missing: $currentPath"
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description must not traverse a reparse point: $currentPath"
        }

        if ($currentPath.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        $parent = [System.IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) {
            throw "$Description did not resolve beneath its controlled root."
        }

        $currentPath = $parent.FullName
    }
}

function Get-ReleaseRegularFiles([string] $Root) {
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "Release file tree root must be a directory: $rootFull"
    }

    $pathRoot = [System.IO.Path]::GetPathRoot($rootFull)
    Assert-NoReparsePointInControlledPath -Root $pathRoot -Path $rootFull -Description "Release file tree"

    $pendingDirectories = [System.Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($rootFull)
    while ($pendingDirectories.Count -gt 0) {
        $directoryPath = $pendingDirectories.Pop()
        foreach ($entryPath in [System.IO.Directory]::EnumerateFileSystemEntries($directoryPath)) {
            $entryFull = [System.IO.Path]::GetFullPath($entryPath)
            if (-not (Test-ReleasePathInside -Root $rootFull -Path $entryFull)) {
                throw "Release file tree entry escaped its root: $entryFull"
            }

            $entry = Get-Item -LiteralPath $entryFull -Force
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release file tree must not contain reparse points: $entryFull"
            }

            if ($entry.PSIsContainer) {
                $pendingDirectories.Push($entryFull)
            }
            else {
                $entry
            }
        }
    }
}

function Assert-NoReparsePointTree([string] $Root) {
    $null = @(Get-ReleaseRegularFiles -Root $Root)
}

function Sort-ReleaseManifestEntriesOrdinal([object[]] $Entries) {
    $entriesByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    foreach ($entry in $Entries) {
        $relativePath = [string] $entry.relativePath
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            throw "Release manifest entries must have a non-empty relative path."
        }

        if ($entriesByPath.ContainsKey($relativePath)) {
            throw "Release manifest contains a duplicate relative path: $relativePath"
        }

        $entriesByPath.Add($relativePath, $entry)
    }

    $relativePaths = [string[]] @($entriesByPath.Keys)
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)
    foreach ($relativePath in $relativePaths) {
        $entriesByPath[$relativePath]
    }
}

function Get-FileSha256([string] $Path) {
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "")
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Complete-ReleaseLaunchSmokeProcess(
    [System.Diagnostics.Process] $Process,
    [bool] $AllowForceTermination,
    [scriptblock] $CloseAppWindow = $null) {
    $processId = $Process.Id
    $Process.Refresh()
    if ($Process.HasExited) {
        return
    }

    $closeRequestFailed = $false
    try {
        if ($null -ne $CloseAppWindow) {
            $closeResult = & $CloseAppWindow
            if ($closeResult -is [bool] -and -not $closeResult) {
                $closeRequestFailed = $true
            }
        }
        elseif (-not $Process.CloseMainWindow()) {
            $closeRequestFailed = $true
        }
    }
    catch {
        $closeRequestFailed = $true
    }

    [void] $Process.WaitForExit(5000)
    $Process.Refresh()
    if ($Process.HasExited) {
        if ($closeRequestFailed) {
            throw "Launch smoke process PID $processId failed its normal window close request."
        }

        return
    }

    if (-not $AllowForceTermination) {
        if ($closeRequestFailed) {
            throw "Safe launch smoke process PID $processId had a failed normal close request and remained alive; it was not force-terminated. Close that process before retrying."
        }

        throw "Safe launch smoke process PID $processId remained alive after graceful close; it was not force-terminated. Close that process before retrying."
    }

    Stop-Process -Id $processId -Force -ErrorAction Stop
    if (-not $Process.WaitForExit(5000)) {
        throw "Launch smoke process PID $processId remained alive after force termination."
    }

    if ($closeRequestFailed) {
        throw "Launch smoke process PID $processId failed its normal window close request."
    }
}

function Get-PeArchitecture([string] $Path) {
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "File is not a valid PE image: $Path"
            }

            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
                throw "PE image has an invalid header offset: $Path"
            }

            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "PE image is missing its signature: $Path"
            }

            $machine = $reader.ReadUInt16()
            if ($machine -eq 0x8664) {
                return "AMD64"
            }

            return "0x{0:X4}" -f $machine
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-WinX64MpvArchitecture([string] $Path) {
    $architecture = Get-PeArchitecture -Path $Path
    if (-not $architecture.Equals("AMD64", [StringComparison]::Ordinal)) {
        throw "MPV runtime must be an actual AMD64 PE image for win-x64; found $architecture."
    }

    return $architecture
}

function Assert-MpvRuntime([string] $BinaryPath, [object] $Manifest) {
    if (-not (Test-Path -LiteralPath $BinaryPath -PathType Leaf)) {
        throw "MPV runtime is missing: $BinaryPath"
    }

    $binaryItem = Get-Item -LiteralPath $BinaryPath -Force
    if (($binaryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "MPV runtime must not be a reparse point."
    }

    if ($binaryItem.Length -ne [long] $Manifest.binary.sizeBytes) {
        throw "MPV runtime size does not match mpv-runtime.json."
    }

    $actualHash = Get-FileSha256 -Path $BinaryPath
    if (-not $actualHash.Equals([string] $Manifest.binary.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MPV runtime SHA256 does not match mpv-runtime.json."
    }

    $actualVersion = $binaryItem.VersionInfo.FileVersion
    if (-not [string]::Equals([string] $actualVersion, [string] $Manifest.binary.fileVersion, [StringComparison]::Ordinal)) {
        throw "MPV runtime file version does not match mpv-runtime.json."
    }

    $actualArchitecture = Assert-WinX64MpvArchitecture -Path $BinaryPath
    if (-not $actualArchitecture.Equals([string] $Manifest.binary.architecture, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MPV runtime architecture does not match mpv-runtime.json."
    }

    return [ordered]@{
        fileName = $binaryItem.Name
        sizeBytes = $binaryItem.Length
        sha256 = $actualHash
        fileVersion = $actualVersion
        architecture = $actualArchitecture
    }
}

function Assert-MpvRuntimeDependencies(
    [string] $RuntimeDirectory,
    [object] $Manifest,
    [switch] $RequireExactFileSet) {
    $runtimeRoot = [System.IO.Path]::GetFullPath($RuntimeDirectory)
    Assert-NoReparsePointInControlledPath -Root ([System.IO.Path]::GetPathRoot($runtimeRoot)) -Path $runtimeRoot -Description "MPV runtime directory"
    $fileNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void] $fileNames.Add("libmpv-2.dll")
    $dependenciesProperty = $Manifest.PSObject.Properties["dependencies"]
    $dependencies = @()
    if ($null -ne $dependenciesProperty) {
        if ($dependenciesProperty.Value -isnot [Array]) {
            throw "MPV dependencies must be an array."
        }
        $dependencies = $dependenciesProperty.Value
    }

    $verified = @()
    foreach ($dependency in $dependencies) {
        $fileName = [string] $dependency.fileName
        $expectedHash = [string] $dependency.sha256
        if ($fileName -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.+-]*\.dll$' -or
            -not $fileNames.Add($fileName) -or $expectedHash -notmatch '^[A-Fa-f0-9]{64}$' -or
            [long] $dependency.sizeBytes -le 0) {
            throw "MPV dependency entry is incomplete, duplicated, or unsafe."
        }

        $path = [System.IO.Path]::GetFullPath((Join-Path $runtimeRoot $fileName))
        if (-not (Test-ReleasePathInside -Root $runtimeRoot -Path $path) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "MPV dependency must be a regular DLL in the runtime directory: $fileName"
        }
        Assert-NoReparsePointInControlledPath -Root $runtimeRoot -Path $path -Description "MPV dependency"
        $item = Get-Item -LiteralPath $path -Force
        $hash = Get-FileSha256 -Path $path
        if ($item.Length -ne [long] $dependency.sizeBytes -or
            -not $hash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "MPV dependency size or SHA256 does not match mpv-runtime.json: $fileName"
        }
        $architecture = Assert-WinX64MpvArchitecture -Path $path
        $verified += [ordered]@{
            fileName = $fileName
            sizeBytes = $item.Length
            sha256 = $hash
            architecture = $architecture
        }
    }

    if ($RequireExactFileSet) {
        foreach ($item in Get-ChildItem -LiteralPath $runtimeRoot -Force) {
            if ($item.Name.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase) -and
                -not $fileNames.Contains($item.Name)) {
                throw "MPV runtime contains an undeclared DLL: $($item.Name)"
            }
        }
    }
    return $verified
}

function Assert-NoReparsePointInFilePath([string] $WorkspaceRoot, [string] $FilePath) {
    Assert-NoReparsePointInControlledPath `
        -Root $WorkspaceRoot `
        -Path $FilePath `
        -Description "Release evidence path"
}

function Get-MpvLicenseEvidence(
    [object] $Manifest,
    [string] $ManifestPath,
    [string] $WorkspaceRoot) {
    $manifestDirectory = Split-Path -Parent $ManifestPath
    $licenseFiles = @($Manifest.license.files)
    if ($licenseFiles.Count -eq 0) {
        return @()
    }

    $evidence = @()
    $packagePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($licenseFile in $licenseFiles) {
        $relativePath = [string] $licenseFile.path
        $expectedHash = [string] $licenseFile.sha256
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [System.IO.Path]::IsPathRooted($relativePath) -or [string]::IsNullOrWhiteSpace($expectedHash) -or $expectedHash -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "MPV license evidence entry is incomplete or unsafe."
        }

        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $manifestDirectory $relativePath))
        if (-not (Test-ReleasePathInside -Root $WorkspaceRoot -Path $sourcePath) -or -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "MPV license evidence must be a regular file inside the workspace: $relativePath"
        }

        Assert-NoReparsePointInFilePath -WorkspaceRoot $WorkspaceRoot -FilePath $sourcePath
        $sourceItem = Get-Item -LiteralPath $sourcePath -Force
        $actualHash = Get-FileSha256 -Path $sourcePath
        if ($sourceItem.Length -ne [long] $licenseFile.sizeBytes -or -not $actualHash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "MPV license evidence size or SHA256 does not match mpv-runtime.json: $relativePath"
        }

        $manifestDirectoryFull = [System.IO.Path]::GetFullPath($manifestDirectory)
        $manifestDirectoryPrefix = $manifestDirectoryFull.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $sourcePath.StartsWith($manifestDirectoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "MPV license evidence must remain beneath the runtime manifest directory: $relativePath"
        }

        $manifestRelativePath = $sourcePath.Substring($manifestDirectoryPrefix.Length).Replace('\', '/')

        $packagePath = "third-party/mpv/$manifestRelativePath"
        if (-not $packagePaths.Add($packagePath)) {
            throw "MPV license evidence has a duplicate package path: $packagePath"
        }

        $evidence += [pscustomobject]@{
            sourcePath = $sourcePath
            sourceRelativePath = $manifestRelativePath
            packagePath = $packagePath
            sizeBytes = $sourceItem.Length
            sha256 = $actualHash
        }
    }

    return @($evidence)
}

function Test-MpvPublicDistributionReady([object] $Manifest, [object[]] $LicenseEvidence) {
    if (-not ([string] $Manifest.provenanceStatus).Equals("verified", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $sourceUri = $null
    $hasSourceUrl = [Uri]::TryCreate([string] $Manifest.sourceUrl, [UriKind]::Absolute, [ref] $sourceUri) -and ($sourceUri.Scheme -eq [Uri]::UriSchemeHttps -or $sourceUri.Scheme -eq [Uri]::UriSchemeHttp)
    $hasBuildRecipeRevision = -not [string]::IsNullOrWhiteSpace([string] $Manifest.buildRecipeRevision)
    $hasLicenseExpression = -not [string]::IsNullOrWhiteSpace([string] $Manifest.license.expression)
    return $hasSourceUrl -and $hasBuildRecipeRevision -and $hasLicenseExpression -and $LicenseEvidence.Count -gt 0
}

function Copy-DotNetRuntimeLicenseEvidence(
    [string] $RuntimeConfigPath,
    [string] $GlobalPackagesRoot,
    [string] $PublishDirectory) {
    $GlobalPackagesRoot = [System.IO.Path]::GetFullPath($GlobalPackagesRoot)
    if ($GlobalPackagesRoot.Length -gt [System.IO.Path]::GetPathRoot($GlobalPackagesRoot).Length) {
        $GlobalPackagesRoot = $GlobalPackagesRoot.TrimEnd('\', '/')
    }
    $config = Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json
    $frameworks = @($config.runtimeOptions.includedFrameworks)
    $requiredFrameworks = @{
        "Microsoft.NETCore.App" = @("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT")
        "Microsoft.WindowsDesktop.App" = @("LICENSE")
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $evidence = @()
    foreach ($framework in $frameworks) {
        $name = [string] $framework.name
        $version = [string] $framework.version
        if (-not $requiredFrameworks.ContainsKey($name) -or -not $seen.Add($name) -or
            $version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$') {
            throw "Self-contained runtimeconfig has an unsupported, duplicated, or unsafe framework entry."
        }
        $packageId = $name.ToLowerInvariant() + ".runtime.win-x64"
        $packageRoot = Join-Path (Join-Path $GlobalPackagesRoot $packageId) $version.ToLowerInvariant()
        foreach ($fileName in $requiredFrameworks[$name]) {
            $source = Join-Path $packageRoot $fileName
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Self-contained .NET license evidence is missing for exact runtime version: $packageId/$version/$fileName"
            }
            Assert-NoReparsePointInControlledPath -Root $GlobalPackagesRoot -Path $source -Description ".NET license source"
            $item = Get-Item -LiteralPath $source -Force
            $hash = Get-FileSha256 -Path $source
            $packagePath = "third-party/dotnet/$name/$version/$fileName"
            $destination = [System.IO.Path]::GetFullPath((Join-Path $PublishDirectory $packagePath))
            if (-not (Test-ReleasePathInside -Root $PublishDirectory -Path $destination)) {
                throw ".NET license destination escaped the publish directory."
            }
            $destinationDirectory = Split-Path -Parent $destination
            [void] [System.IO.Directory]::CreateDirectory($destinationDirectory)
            Assert-NoReparsePointInControlledPath -Root $PublishDirectory -Path $destinationDirectory -Description ".NET license destination"
            if (Test-Path -LiteralPath $destination) {
                throw ".NET license destination already exists: $packagePath"
            }
            Copy-Item -LiteralPath $source -Destination $destination
            if ((Get-Item -LiteralPath $destination).Length -ne $item.Length -or
                -not (Get-FileSha256 -Path $destination).Equals($hash, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Published .NET license evidence differs from its NuGet input: $packagePath"
            }
            $evidence += [ordered]@{
                framework = $name
                version = $version
                source = "nuget:$packageId/$version/$fileName"
                packagePath = $packagePath
                sizeBytes = $item.Length
                sha256 = $hash
            }
        }
    }
    if ($seen.Count -ne $requiredFrameworks.Count) {
        throw "Self-contained runtimeconfig must identify both .NETCore and WindowsDesktop framework versions."
    }
    return $evidence
}

function Copy-MpvLicenseEvidence([object[]] $LicenseEvidence, [string] $PublishDirectory) {
    foreach ($license in $LicenseEvidence) {
        $relativeNativePath = ([string] $license.packagePath).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $PublishDirectory $relativeNativePath))
        if (-not (Test-ReleasePathInside -Root $PublishDirectory -Path $destinationPath)) {
            throw "MPV license package path escaped the publish directory."
        }

        $destinationDirectory = Split-Path -Parent $destinationPath
        [void] [System.IO.Directory]::CreateDirectory($destinationDirectory)
        Copy-Item -LiteralPath ([string] $license.sourcePath) -Destination $destinationPath -Force
        $destinationItem = Get-Item -LiteralPath $destinationPath -Force
        $destinationHash = Get-FileSha256 -Path $destinationPath
        if ($destinationItem.Length -ne [long] $license.sizeBytes -or -not $destinationHash.Equals([string] $license.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Published MPV license evidence does not match its tracked input: $($license.packagePath)"
        }
    }
}
