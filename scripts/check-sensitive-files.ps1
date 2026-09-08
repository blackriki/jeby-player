[CmdletBinding()]
param(
    [string] $PayloadRoot,
    [string[]] $AdditionalFile = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$workspaceRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$workspacePrefix = $workspaceRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$validationScript = Join-Path $workspaceRoot "scripts\release-validation.ps1"
. $validationScript
$maximumTextBytes = 4MB
$textExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @(
        ".cs", ".csproj", ".config", ".editorconfig", ".ini", ".json", ".lock",
        ".md", ".props", ".ps1", ".psd1", ".psm1", ".sln", ".targets", ".toml",
        ".txt", ".xaml", ".xml", ".yaml", ".yml"
    ),
    [StringComparer]::OrdinalIgnoreCase)
$textFileNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @("LICENSE", "NOTICE", "README", "global.json"),
    [StringComparer]::OrdinalIgnoreCase)
$blockedRepositoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @("settings.json", ".env"),
    [StringComparer]::OrdinalIgnoreCase)
$blockedRepositoryExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [string[]] @(".key", ".p12", ".pfx", ".pem"),
    [StringComparer]::OrdinalIgnoreCase)
$contentRules = @(
    @{ Name = "private key"; Pattern = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' },
    @{ Name = "api_key query"; Pattern = '(?i)api_key\s*=\s*[^&\s"'']{8,}' },
    @{ Name = "AccessToken literal"; Pattern = '(?i)AccessToken["'']?\s*[:=]\s*["''][^"''<>{}\s]{12,}["'']' },
    @{ Name = "X-Emby-Token literal"; Pattern = '(?i)X-Emby-Token["'']?\s*[:=\]]+\s*["''][^"''<>{}\s]{12,}["'']' },
    @{ Name = "credential assignment"; Pattern = '(?i)(?:client_secret|access_token|refresh_token|password)["'']?\s*[:=]\s*["''][^"''<>{}\s]{12,}["'']' },
    @{ Name = "JWT-like token"; Pattern = 'eyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{8,}' },
    @{ Name = "hardcoded private server url"; Pattern = '(?i)https?://(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[0-1])\.\d{1,3}\.\d{1,3})' }
)

function Test-IsInsideWorkspace([string] $Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    return $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Test-IsTextCandidate([System.IO.FileInfo] $File) {
    if ($textExtensions.Contains($File.Extension) -or $textFileNames.Contains($File.Name)) {
        return $true
    }

    return $File.Name.EndsWith(".deps.json", [StringComparison]::OrdinalIgnoreCase)
        -or $File.Name.EndsWith(".runtimeconfig.json", [StringComparison]::OrdinalIgnoreCase)
}

function Test-ContainsNullByte([string] $Path) {
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $buffer = [byte[]]::new(8192)
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            for ($index = 0; $index -lt $count; $index++) {
                if ($buffer[$index] -eq 0) {
                    return $true
                }
            }
        }

        return $false
    }
    finally {
        $stream.Dispose()
    }
}

function Remove-KnownSafeFixtureText([string] $RelativePath, [string] $Content) {
    $sanitized = $Content.Replace("http://192.168.1.100:8096", "http://example.invalid")
    $normalizedPath = $RelativePath.Replace('\', '/')
    if ($normalizedPath.StartsWith("tests/", [StringComparison]::OrdinalIgnoreCase)) {
        $apiKeyName = "api_" + "key"
        $sanitized = $sanitized.Replace("$apiKeyName=secret-token", "fixture_query=<test-fixture>")
        $sanitized = $sanitized.Replace("$apiKeyName={secretToken}", "fixture_query=<test-fixture>")
        $sanitized = $sanitized.Replace("$apiKeyName=test-access-token", "fixture_query=<test-fixture>")
        $sanitized = $sanitized.Replace('"test-access-token"', '"<test-fixture>"')
        $sanitized = $sanitized.Replace('"stale-password"', '"<test-fixture>"')
        $sanitized = $sanitized.Replace('"new-password"', '"<test-fixture>"')
        $sanitized = $sanitized.Replace('"wrong-password"', '"<test-fixture>"')
        $sanitized = $sanitized.Replace('"draft-password"', '"<test-fixture>"')
    }

    return $sanitized
}

function Get-RepositoryFiles {
    $gitRoot = ((& git -C $workspaceRoot rev-parse --show-toplevel) -join "").Trim()
    if ($LASTEXITCODE -ne 0 -or -not ([System.IO.Path]::GetFullPath($gitRoot)).Equals($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Sensitive scan must run from its repository workspace."
    }

    $relativePaths = @(& git -C $workspaceRoot -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate tracked and non-ignored untracked files."
    }

    foreach ($relativePathValue in $relativePaths) {
        $relativePath = [string] $relativePathValue
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $workspaceRoot $relativePath))
        if (-not (Test-IsInsideWorkspace -Path $fullPath) -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Git reported an unsafe or missing repository file: $relativePath"
        }

        Assert-NoReparsePointInControlledPath `
            -Root $workspaceRoot `
            -Path $fullPath `
            -Description "Repository sensitive-scan path"
        $file = Get-Item -LiteralPath $fullPath -Force
        if ($blockedRepositoryNames.Contains($file.Name) -or $blockedRepositoryExtensions.Contains($file.Extension)) {
            throw "Potential credential file is tracked or non-ignored: $relativePath"
        }

        [pscustomobject]@{ File = $file; RelativePath = $relativePath.Replace('\', '/') }
    }
}

function Get-PayloadFiles([string] $Root) {
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "PayloadRoot must be a regular directory."
    }

    $rootItem = Get-Item -LiteralPath $rootFull -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "PayloadRoot must be a regular directory."
    }

    $rootPrefix = $rootFull.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in Get-ReleaseRegularFiles -Root $rootFull) {
        [pscustomobject]@{
            File = $file
            RelativePath = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        }
    }
}

$scanEntries = if ([string]::IsNullOrWhiteSpace($PayloadRoot)) {
    @(Get-RepositoryFiles)
}
else {
    @(Get-PayloadFiles -Root $PayloadRoot)
}

foreach ($additionalPathValue in $AdditionalFile) {
    $additionalPath = [System.IO.Path]::GetFullPath($additionalPathValue)
    if (-not (Test-Path -LiteralPath $additionalPath -PathType Leaf)) {
        throw "Additional sensitive-scan input must be a regular file: $additionalPath"
    }

    Assert-NoReparsePointInControlledPath `
        -Root ([System.IO.Path]::GetPathRoot($additionalPath)) `
        -Path $additionalPath `
        -Description "Additional sensitive-scan input"
    $additionalItem = Get-Item -LiteralPath $additionalPath -Force
    if ($additionalItem.PSIsContainer) {
        throw "Additional sensitive-scan input must be a regular file: $additionalPath"
    }

    $scanEntries += [pscustomobject]@{ File = $additionalItem; RelativePath = $additionalItem.Name }
}

$textScanned = 0
$binaryOrUnsupportedSkipped = 0
$matches = @()
foreach ($entry in $scanEntries) {
    $file = [System.IO.FileInfo] $entry.File
    if (-not (Test-IsTextCandidate -File $file)) {
        $binaryOrUnsupportedSkipped++
        continue
    }

    if ($file.Length -gt $maximumTextBytes) {
        throw "Text candidate exceeds the fail-closed $maximumTextBytes-byte scan limit: $($entry.RelativePath)"
    }

    if (Test-ContainsNullByte -Path $file.FullName) {
        throw "Text candidate contains a NUL byte and cannot be scanned safely: $($entry.RelativePath)"
    }

    $content = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $content = Remove-KnownSafeFixtureText -RelativePath ([string] $entry.RelativePath) -Content $content
    $textScanned++
    foreach ($rule in $contentRules) {
        if ([regex]::IsMatch($content, [string] $rule.Pattern)) {
            $matches += "$($entry.RelativePath): $($rule.Name)"
        }
    }
}

if ($matches.Count -gt 0) {
    throw ("Potential sensitive content detected:`n" + (($matches | Sort-Object -Unique) -join "`n"))
}

Write-Output "Sensitive scan passed: text=$textScanned skipped-binary-or-unsupported=$binaryOrUnsupportedSkipped."
