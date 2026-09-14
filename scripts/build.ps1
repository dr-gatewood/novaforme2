# Builds and publishes Nova4Me2 (desktop app + CLI) as self-contained win-x64 folders under .\publish
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\build.ps1 [-Configuration Release]
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
dotnet --version | Out-Null
dotnet restore Nova4Me2.sln
dotnet build Nova4Me2.sln -c $Configuration --no-restore
dotnet test tests/Nova4Me2.Tests -c $Configuration --no-build
dotnet publish src/Nova4Me2.App/Nova4Me2.App.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish/Nova4Me2
dotnet publish src/Nova4Me2.Cli/Nova4Me2.Cli.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/nova4me2-cli
Write-Host ""
Write-Host "Desktop app : publish\Nova4Me2\Nova4Me2.exe   (right-click > Run as administrator, or it will prompt)"
Write-Host "CLI         : publish\nova4me2-cli\nova4me2.exe"
Write-Host "Optional    : install WinFsp from https://winfsp.dev/rel/ to enable 'Mount as drive letter'."
