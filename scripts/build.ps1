#!/usr/bin/env pwsh
# Restore, build and test everything. Usage: scripts/build.ps1 [-Configuration Debug|Release]
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
dotnet restore; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build -c $Configuration --no-restore; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test -c $Configuration --no-build --logger 'trx;LogFileName=results.trx' --results-directory artifacts/test-results
exit $LASTEXITCODE
