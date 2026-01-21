# Server Installer Script for Windows
# Run as Administrator
# Usage: .\install-server.ps1

param(
    [int]$HttpPort = 5081,
    [int]$DiscoveryPort = 5082,
    [string]$InstallPath = "C:\ClubServer",
    [string]$PostgresConnectionString = ""
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
Write-Host "  Club Management System - Server Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Create installation directory
Write-Host "[1/5] Creating installation directory..." -ForegroundColor Yellow
if (!(Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

# Step 2: Copy server files
Write-Host "[2/5] Copying server files..." -ForegroundColor Yellow
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverFiles = Join-Path $scriptDir "server"

if (Test-Path $serverFiles) {
    Copy-Item -Path "$serverFiles\*" -Destination $InstallPath -Recurse -Force
} else {
    Write-Error "Server files not found at: $serverFiles"
    Write-Host "Please run this script from the installer package directory."
    exit 1
}

# Step 3: Configure appsettings
Write-Host "[3/5] Configuring server..." -ForegroundColor Yellow
$appsettings = Join-Path $InstallPath "appsettings.json"

$config = @{
    Logging = @{
        LogLevel = @{
            Default = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
        }
    }
    ConnectionStrings = @{
        DefaultConnection = if ($PostgresConnectionString) { $PostgresConnectionString } else { "Host=localhost;Database=cms;Username=postgres;Password=postgres" }
    }
    Discovery = @{
        Enabled = $true
        HttpPort = $HttpPort
    }
    Urls = "http://0.0.0.0:$HttpPort"
}

$config | ConvertTo-Json -Depth 5 | Set-Content -Path $appsettings

# Step 4: Open firewall ports
Write-Host "[4/5] Configuring firewall..." -ForegroundColor Yellow
try {
    # HTTP API port
    Remove-NetFirewallRule -DisplayName "ClubServer HTTP" -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "ClubServer HTTP" -Direction Inbound -Protocol TCP -LocalPort $HttpPort -Action Allow | Out-Null
    
    # UDP Discovery port
    Remove-NetFirewallRule -DisplayName "ClubServer Discovery" -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "ClubServer Discovery" -Direction Inbound -Protocol UDP -LocalPort $DiscoveryPort -Action Allow | Out-Null
    
    Write-Host "   Opened ports: TCP $HttpPort, UDP $DiscoveryPort" -ForegroundColor Gray
} catch {
    Write-Warning "Failed to configure firewall. You may need to manually open ports $HttpPort (TCP) and $DiscoveryPort (UDP)."
}

# Step 5: Install and start Windows Service
Write-Host "[5/5] Installing Windows Service..." -ForegroundColor Yellow
$serviceName = "ClubServer"
$exePath = Join-Path $InstallPath "Cms.Server.exe"

# Stop and remove existing service
try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch {}
try { sc.exe delete $serviceName 2>$null | Out-Null } catch {}

Start-Sleep -Seconds 2

# Create new service
$binPath = "`"$exePath`""
sc.exe create $serviceName binPath= $binPath start= auto | Out-Null
sc.exe description $serviceName "Club Management System Server" | Out-Null
sc.exe start $serviceName | Out-Null

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Server Installation Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Server URL: http://localhost:$HttpPort" -ForegroundColor Cyan
Write-Host "API Docs:   http://localhost:$HttpPort/swagger" -ForegroundColor Cyan
Write-Host ""
Write-Host "Client PCs will auto-discover this server on the LAN." -ForegroundColor White
Write-Host "Firewall rules have been created for ports $HttpPort and $DiscoveryPort." -ForegroundColor White
Write-Host ""
