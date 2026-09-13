param(
    [switch] $PrintCommand,
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/EmbyPlayer.App/EmbyPlayer.App.csproj"
$outputDir = Join-Path $root "src/EmbyPlayer.App/bin/Debug/net8.0-windows"
$outputDirFull = [System.IO.Path]::GetFullPath($outputDir)
$arguments = @("run", "--project", $project, "--no-build")

if ($PrintCommand) {
    Write-Output ("dotnet " + ($arguments -join " "))
    return
}

Push-Location $root
try {
    if (-not $NoBuild) {
        dotnet build $project --no-restore -v minimal -m:1
    }

    Get-Process -Name "JebyPlayer", "EmbyPlayer.App" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Path -and
            [System.IO.Path]::GetFullPath($_.Path).StartsWith($outputDirFull, [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            Write-Output ("Stopping existing app process " + $_.Id)
            Stop-Process -Id $_.Id -Force
        }

    Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $root
}
finally {
    Pop-Location
}
