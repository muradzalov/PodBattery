<#
.SYNOPSIS
  Installs or updates PodBattery from the latest GitHub release. No admin rights and no .NET needed.
.DESCRIPTION
  Downloads PodBattery.exe, checks its SHA-256 against the release, puts it in
  %LOCALAPPDATA%\Programs\PodBattery, adds Start menu and desktop shortcuts, sets it to start
  with Windows, and launches it. Run it again to update.

    irm https://raw.githubusercontent.com/muradzalov/PodBattery/main/get.ps1 | iex

  With options:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/muradzalov/PodBattery/main/get.ps1))) -NoStartup -NoDesktopShortcut
#>

# Everything runs inside a function so that, when piped into iex, it doesn't change settings in your
# PowerShell session or close it on an error.
function Install-PodBattery {
    param([switch]$NoStartup, [switch]$NoDesktopShortcut)

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell downloads far faster without the progress bar
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $release = 'https://github.com/muradzalov/PodBattery/releases/latest/download'
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\PodBattery'
    $exe = Join-Path $dest 'PodBattery.exe'
    $download = Join-Path ([IO.Path]::GetTempPath()) "PodBattery-$PID.exe"
    $checksum = "$download.sha256"

    try {
        Write-Host 'Downloading PodBattery...'
        Invoke-WebRequest "$release/PodBattery.exe" -OutFile $download -UseBasicParsing
        Invoke-WebRequest "$release/PodBattery.exe.sha256" -OutFile $checksum -UseBasicParsing

        $expected = (Get-Content $checksum -Raw).Trim().Split()[0]
        $actual = (Get-FileHash $download -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "The download doesn't match the release checksum, so nothing was installed." }

        # Close this user's running copy (not other users' on a shared PC) so it can be replaced.
        $session = (Get-Process -Id $PID).SessionId
        Get-Process PodBattery -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session | ForEach-Object {
            $_ | Stop-Process -Force
            $_.WaitForExit(5000) | Out-Null
        }

        New-Item -ItemType Directory -Force $dest | Out-Null
        Copy-Item $download $exe -Force

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
        Write-Host "PodBattery $((Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]) installed to $dest"
        Write-Host 'Open your AirPods case next to this PC so it can find them.'
    }
    finally {
        Remove-Item $download, $checksum -ErrorAction SilentlyContinue
    }
}

Install-PodBattery @args
