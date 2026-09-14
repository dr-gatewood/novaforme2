#!/usr/bin/env bash
# Linux/macOS: builds everything (the WPF app compiles here too but only runs on Windows) and runs the test-suite.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore Nova4Me2.sln
dotnet build Nova4Me2.sln -c Release --no-restore
dotnet test tests/Nova4Me2.Tests -c Release --no-build
dotnet publish src/Nova4Me2.Cli/Nova4Me2.Cli.csproj -c Release -o publish/nova4me2-cli
echo "CLI: publish/nova4me2-cli/nova4me2 (dotnet publish/nova4me2-cli/nova4me2.dll ...)"
