$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\CodexUsageHub\CodexUsageHub.csproj"
$out = Join-Path $root "publish\win-x64"

if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

Write-Host "Publishing Codex Usage Hub (Windows x64, self-contained)..."
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

Write-Host ""
Write-Host "Done: $out\CodexUsageHub.exe"
