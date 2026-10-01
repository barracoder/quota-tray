#!/usr/bin/env pwsh
# Build the installer for one runtime from a previous scripts/publish.ps1 run.
#   osx-*  → artifacts/<rid>/quota-tray-<rid>.dmg        (needs macOS: hdiutil)
#   win-*  → artifacts/<rid>/quota-tray-<rid>-setup.exe  (needs Windows: Inno Setup 6)
# Usage: scripts/package.ps1 [-Rid win-x64|win-arm64|osx-arm64|osx-x64]   (default: current OS/arch)
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

if ($Rid -like 'osx-*') {
  $app = "$out/QuotaTray.app"
  if (-not (Test-Path $app)) { throw "missing $app - run scripts/publish.ps1 -Rid $Rid first" }
  if (-not $IsMacOS) { throw 'DMG creation needs macOS (hdiutil).' }
  $dmg = "$out/quota-tray-$Rid.dmg"
  $stage = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
  New-Item -ItemType Directory -Path $stage | Out-Null
  Copy-Item $app $stage -Recurse
  ln -s /Applications "$stage/Applications"
  if (Test-Path $dmg) { Remove-Item $dmg }
  hdiutil create -quiet -volname "QuotaTray $version" -srcfolder $stage -ov -format UDZO $dmg
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  Remove-Item $stage -Recurse -Force
  Write-Host "Installer: $dmg"
}
elseif ($Rid -like 'win-*') {
  if (-not (Test-Path "$out/publish")) { throw "missing $out/publish - run scripts/publish.ps1 -Rid $Rid first" }
  $isArch = if ($Rid -eq 'win-x64') { 'x64compatible' } else { 'arm64' }
  $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
  if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
  if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found (ISCC.exe). Install from https://jrsoftware.org/isinfo.php or 'choco install innosetup'." }
  & $iscc "/DRid=$Rid" "/DArch=$isArch" "/DVersion=$version" "/Q" "installer\windows\quota-tray.iss"
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  Write-Host "Installer: $out/quota-tray-$Rid-setup.exe"
}
else {
  throw "No installer defined for $Rid"
}
