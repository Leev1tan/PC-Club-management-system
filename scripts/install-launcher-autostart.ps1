param(
    [string]$LauncherPath = 'C:\ClubLauncher\Cms.Launcher.exe'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $LauncherPath)) {
    Write-Error "Launcher not found at $LauncherPath"
    exit 1
}

$StartupFolder = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
$ShortcutPath = Join-Path $StartupFolder "ClubLauncher.lnk"

Write-Host "Creating startup shortcut at $ShortcutPath..." -ForegroundColor Cyan

$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = $LauncherPath
$Shortcut.WorkingDirectory = Split-Path $LauncherPath
$Shortcut.Description = "Club Management Launcher"
$Shortcut.Save()

Write-Host "Launcher will start automatically at user logon." -ForegroundColor Green
Write-Host "To remove: delete $ShortcutPath" -ForegroundColor Yellow

