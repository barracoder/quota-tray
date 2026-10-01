#!/usr/bin/env pwsh
# Publish a self-contained single-file build for one runtime.
# Usage: scripts/publish.ps1 [-Rid win-x64|win-arm64|osx-arm64|osx-x64]   (default: current OS/arch)
# Output: artifacts/<rid>/ and, for osx-*, artifacts/<rid>/QuotaTray.app
[CmdletBinding()]
param([string]$Rid)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (-not $Rid) {
  $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
  $os = if ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { 'win' }
  $Rid = "$os-$arch"
}

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$out = "artifacts/$Rid"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src/QuotaTray.App/QuotaTray.App.csproj -c Release -r $Rid -o "$out/publish"
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if ($Rid -like 'osx-*') {
  $app = "$out/QuotaTray.app"
  New-Item -ItemType Directory -Force -Path "$app/Contents/MacOS", "$app/Contents/Resources" | Out-Null
  Copy-Item "$out/publish/*" "$app/Contents/MacOS/" -Recurse -Force
  (Get-Content src/QuotaTray.App/macos/Info.plist -Raw).Replace('__VERSION__', $version) | Set-Content "$app/Contents/Info.plist" -NoNewline
  if ($IsMacOS) {
    codesign --force --deep --sign - $app
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
  } else {
    Write-Warning 'Not on macOS: bundle created but not code-signed.'
  }
  Write-Host "Bundle: $app"
}

Write-Host "Published $Rid v$version to $out"
