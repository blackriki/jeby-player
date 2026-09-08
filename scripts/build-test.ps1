param(
    [switch] $SkipTests,
    [switch] $UiOnly,
    [switch] $CoreOnly,
    [switch] $EmbyOnly,
    [switch] $FullNoPlayer,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",
    [string] $ArtifactsPath,
    [string] $ResultsDirectory
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifactArguments = @()
if (-not [string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $artifactArguments = @("--artifacts-path", [System.IO.Path]::GetFullPath($ArtifactsPath))
}

function Assert-NativeSuccess([string] $operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$operation failed with exit code $LASTEXITCODE."
    }
}

Push-Location $root
try {
    dotnet build --no-restore -c $Configuration -v minimal -m:1 -nr:false -p:UseSharedCompilation=false @artifactArguments
    Assert-NativeSuccess "dotnet build"
    if (-not $SkipTests) {
        $testProjects = @()
        if ($UiOnly) {
            $testProjects = @("tests/EmbyPlayer.UI.Tests/EmbyPlayer.UI.Tests.csproj")
        }
        elseif ($CoreOnly) {
            $testProjects = @("tests/EmbyPlayer.Core.Tests/EmbyPlayer.Core.Tests.csproj")
        }
        elseif ($EmbyOnly) {
            $testProjects = @("tests/EmbyPlayer.Emby.Tests/EmbyPlayer.Emby.Tests.csproj")
        }
        elseif ($FullNoPlayer) {
            $testProjects = @(
                "tests/EmbyPlayer.UI.Tests/EmbyPlayer.UI.Tests.csproj",
                "tests/EmbyPlayer.Emby.Tests/EmbyPlayer.Emby.Tests.csproj",
                "tests/EmbyPlayer.Core.Tests/EmbyPlayer.Core.Tests.csproj"
            )
        }
        else {
            $testProjects = @(
                "tests/EmbyPlayer.UI.Tests/EmbyPlayer.UI.Tests.csproj",
                "tests/EmbyPlayer.Emby.Tests/EmbyPlayer.Emby.Tests.csproj",
                "tests/EmbyPlayer.Core.Tests/EmbyPlayer.Core.Tests.csproj",
                "tests/EmbyPlayer.Player.Tests/EmbyPlayer.Player.Tests.csproj"
            )
        }

        foreach ($project in $testProjects) {
            $resultArguments = @()
            if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
                $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
                $resultArguments = @(
                    "--results-directory", [System.IO.Path]::GetFullPath($ResultsDirectory),
                    "--logger", "trx;LogFileName=$projectName.trx"
                )
            }

            dotnet test $project --no-build -c $Configuration -v minimal -m:1 -nr:false -p:UseSharedCompilation=false @artifactArguments @resultArguments
            Assert-NativeSuccess "dotnet test $project"
        }
    }
}
finally {
    Pop-Location
}
