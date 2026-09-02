[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\OhMyPc.App\OhMyPc.App.csproj"
$artifactRoot = Join-Path $root "artifacts\portable"
$publishDirectory = Join-Path $artifactRoot "OhMyPc-win-x64"
$archivePath = Join-Path $artifactRoot "OhMyPc-win-x64-portable.zip"
$tokscale = Join-Path $root "tools\tokscale\node_modules\@tokscale\cli-win32-x64-msvc\bin\tokscale.exe"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet。请先安装 .NET 10 SDK。"
}

if (-not (Test-Path $tokscale)) {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw "未找到 npm，无法下载打包所需的 tokscale.exe。请先安装 Node.js。"
    }

    Write-Host "正在下载 tokscale..."
    & (Join-Path $PSScriptRoot "fetch-tokscale.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "tokscale 下载失败，退出代码：$LASTEXITCODE"
    }
}

if (Test-Path $artifactRoot) {
    Remove-Item $artifactRoot -Recurse -Force
}
New-Item $publishDirectory -ItemType Directory -Force | Out-Null

Write-Host "正在发布 Windows x64 自包含便携版..."
& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=embedded

if ($LASTEXITCODE -ne 0) {
    throw "发布失败，退出代码：$LASTEXITCODE"
}

$application = Join-Path $publishDirectory "OhMyPc.App.exe"
if (-not (Test-Path $application)) {
    throw "发布完成，但未找到主程序：$application"
}

Write-Host "正在生成 ZIP 压缩包..."
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host ""
Write-Host "便携版打包完成："
Write-Host "  主程序时间：$(Get-Item $application | Select-Object -ExpandProperty LastWriteTime)"
Write-Host "  程序目录：$publishDirectory"
Write-Host "  压缩包：  $archivePath"
