$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $root "tools\tokscale"

npm install --prefix $packageRoot --omit=dev --ignore-scripts

$binary = Join-Path $packageRoot "node_modules\@tokscale\cli-win32-x64-msvc\bin\tokscale.exe"
if (-not (Test-Path $binary)) {
    throw "tokscale.exe was not installed"
}

Write-Host $binary
