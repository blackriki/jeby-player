param(
    [switch] $WhatIf,
    [switch] $DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$isDryRun = $WhatIf -or $DryRun
$targets = Get-ChildItem -Path (Join-Path $root "src"), (Join-Path $root "tests") -Directory -Recurse |
    Where-Object { $_.Name -in @("bin", "obj") }

foreach ($target in $targets) {
    if ($isDryRun) {
        Write-Output ("Would remove " + $target.FullName)
        continue
    }

    Remove-Item -LiteralPath $target.FullName -Recurse -Force
}
