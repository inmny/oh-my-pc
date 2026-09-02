$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $root "tools\tokscale"
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

# npm 需要目标目录里有 package.json 才能以 --prefix 安装（CI 全新检出时不存在）
$manifest = Join-Path $packageRoot "package.json"
if (-not (Test-Path $manifest)) {
    Set-Content -Path $manifest -Value '{"name":"tokscale-bin","version":"0.0.0","private":true}'
}

npm install --prefix $packageRoot "@tokscale/cli-win32-x64-msvc" --omit=dev --ignore-scripts

$binary = Join-Path $packageRoot "node_modules\@tokscale\cli-win32-x64-msvc\bin\tokscale.exe"
if (-not (Test-Path $binary)) {
    throw "tokscale.exe was not installed"
}

Write-Host $binary
