<#
.SYNOPSIS
  Builds PodBattery and installs it for the current user. No admin rights needed.
.DESCRIPTION
  Publishes to %LOCALAPPDATA%\Programs\PodBattery, sets it to start with Windows
  (skip with -NoStartup) and launches it. Re-run to update. Needs the .NET 10 SDK.

  Run it with:  powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param([switch]$NoStartup)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'Programs\PodBattery'
$exe = Join-Path $dest 'PodBattery.exe'
$staging = Join-Path ([IO.Path]::GetTempPath()) "PodBattery-build-$PID"

# Build first, into a temporary folder, so a failed build leaves the installed copy untouched and running.
dotnet publish "$PSScriptRoot\src\PodBattery\PodBattery.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $staging
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was changed.' }

# Close this user's running copy (not other users' on a shared PC) so its exe can be replaced.
$session = (Get-Process -Id $PID).SessionId
Get-Process PodBattery -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session | ForEach-Object {
    $_ | Stop-Process -Force
    $_.WaitForExit(5000) | Out-Null
}

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $staging '*') $dest -Recurse -Force
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoStartup) {
    Remove-ItemProperty $run -Name PodBattery -ErrorAction SilentlyContinue
} else {
    Set-ItemProperty $run -Name PodBattery -Value "`"$exe`""
}

Start-Process $exe
Write-Host "Installed to $dest"
