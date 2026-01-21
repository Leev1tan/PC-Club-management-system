# Client (Gaming PC) Installer Script for Windows
# Run as Administrator
# Usage: .\install-client.ps1 [-ServerUrl "http://server:5081"]

param(
    [string]$ServerUrl = "",  # Empty = auto-discovery
    [string]$AgentPath = "C:\ClubAgent",
    [string]$LauncherPath = "C:\ClubLauncher",
    [switch]$KioskMode = $false
)

$ErrorActionPreference = 'Stop'

function Test-IsAdmin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    Write-Error "Please run this script as Administrator."
    exit 1
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Club Management System - Client Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Step 1: Create directories
Write-Host "[1/6] Creating installation directories..." -ForegroundColor Yellow
foreach ($path in @($AgentPath, $LauncherPath)) {
    if (!(Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

# Step 2: Copy agent files
Write-Host "[2/6] Installing Agent Service..." -ForegroundColor Yellow
$agentFiles = Join-Path $scriptDir "agent"

if (Test-Path $agentFiles) {
    # Stop existing service first
    try { Stop-Service -Name "ClubAgent" -Force -ErrorAction SilentlyContinue } catch {}
    Start-Sleep -Seconds 1
    
    Copy-Item -Path "$agentFiles\*" -Destination $AgentPath -Recurse -Force
} else {
    Write-Error "Agent files not found at: $agentFiles"
    exit 1
}

# Step 3: Configure agent
Write-Host "[3/6] Configuring Agent..." -ForegroundColor Yellow
$agentConfig = Join-Path $AgentPath "appsettings.json"

$config = @{
    Logging = @{
        LogLevel = @{
            Default = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
        }
    }
    Server = @{
        BaseUrl = $ServerUrl  # Empty = auto-discovery
    }
}

$config | ConvertTo-Json -Depth 5 | Set-Content -Path $agentConfig

if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    Write-Host "   Server URL: Auto-discovery (LAN broadcast)" -ForegroundColor Gray
} else {
    Write-Host "   Server URL: $ServerUrl" -ForegroundColor Gray
}

# Step 4: Copy launcher files
Write-Host "[4/6] Installing Launcher..." -ForegroundColor Yellow
$launcherFiles = Join-Path $scriptDir "launcher"

if (Test-Path $launcherFiles) {
    Copy-Item -Path "$launcherFiles\*" -Destination $LauncherPath -Recurse -Force
} else {
    Write-Warning "Launcher files not found at: $launcherFiles. Skipping launcher installation."
}

# Step 5: Install Agent Windows Service
Write-Host "[5/6] Installing Agent Windows Service..." -ForegroundColor Yellow
$serviceName = "ClubAgent"
$exePath = Join-Path $AgentPath "Cms.Agent.Service.exe"

try { sc.exe delete $serviceName 2>$null | Out-Null } catch {}
Start-Sleep -Seconds 1

$binPath = "`"$exePath`""
sc.exe create $serviceName binPath= $binPath start= auto obj= "NT AUTHORITY\LocalService" type= own | Out-Null
sc.exe description $serviceName "Club Agent Service - Gaming PC management agent" | Out-Null
sc.exe start $serviceName | Out-Null

# Step 6: Configure Launcher autostart (optional kiosk mode)
Write-Host "[6/6] Configuring Launcher autostart..." -ForegroundColor Yellow
$launcherExe = Join-Path $LauncherPath "Cms.Launcher.exe"

if (Test-Path $launcherExe) {
    # Add to common startup folder (all users)
    $startupFolder = [Environment]::GetFolderPath('CommonStartup')
    $shortcutPath = Join-Path $startupFolder "ClubLauncher.lnk"
    
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $launcherExe
    $shortcut.WorkingDirectory = $LauncherPath
    $shortcut.Description = "Club Launcher"
    $shortcut.Save()
    
    Write-Host "   Launcher will start automatically on login" -ForegroundColor Gray
    
    if ($KioskMode) {
        Write-Host "   Kiosk mode: Launcher set as shell replacement (requires additional setup)" -ForegroundColor Yellow
        # Registry key to use launcher as shell (replaces explorer.exe)
        # This is commented out as it requires careful consideration
        # Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "Shell" -Value $launcherExe
        Write-Warning "Full kiosk mode requires additional configuration. See documentation."
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Client Installation Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Agent Service: Running (ClubAgent)" -ForegroundColor Cyan
Write-Host "Launcher: Installed at $LauncherPath" -ForegroundColor Cyan
Write-Host ""

if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    Write-Host "The agent will automatically discover the server on your LAN." -ForegroundColor White
    Write-Host "Make sure the server is running and UDP port 5082 is open." -ForegroundColor White
} else {
    Write-Host "The agent is configured to connect to: $ServerUrl" -ForegroundColor White
}

Write-Host ""
Write-Host "You may need to restart the PC for the launcher autostart to take effect." -ForegroundColor Yellow
Write-Host ""
