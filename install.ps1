<#
.SYNOPSIS
  Builds PodBattery and installs it for the current user. No admin rights needed.
.DESCRIPTION
  Publishes to %LOCALAPPDATA%\Programs\PodBattery, adds Start menu and desktop shortcuts
  (skip the desktop one with -NoDesktopShortcut), sets it to start with Windows (skip with
  -NoStartup) and launches it. Re-run to update. Needs the .NET 10 SDK.

  Run it with:  powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param([switch]$NoStartup, [switch]$NoDesktopShortcut)
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

# Shortcuts, so it's easy to start again after choosing Exit: Start menu (search "PodBattery") and desktop.
$shell = New-Object -ComObject WScript.Shell
$folders = @([Environment]::GetFolderPath('Programs'))
if (-not $NoDesktopShortcut) { $folders += [Environment]::GetFolderPath('Desktop') }
foreach ($folder in $folders) {
    $link = $shell.CreateShortcut((Join-Path $folder 'PodBattery.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $dest
    $link.IconLocation = "$exe,0"
    $link.Description = 'AirPods battery indicator for the Windows system tray'
    $link.Save()
}

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoStartup) {
    Remove-ItemProperty $run -Name PodBattery -ErrorAction SilentlyContinue
} else {
    Set-ItemProperty $run -Name PodBattery -Value "`"$exe`""
}

Start-Process $exe
Write-Host "Installed to $dest"
