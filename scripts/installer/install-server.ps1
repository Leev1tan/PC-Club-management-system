# Server Installer Script for Windows
# Run as Administrator
# Usage: .\install-server.ps1 [-PostgresConnectionString "Host=...;Database=cms;Username=...;Password=..."]
#
# Data directory (-DataPath, default C:\ProgramData\PCClub): SQLite database cms.db, login cookie
# keys, one-time setup code. Only SYSTEM and Administrators can open it.
# First start: the installer shows the one-time setup code; open the admin UI and create the Owner.
# -EnrollmentToken: secret for the club PC installer. Empty = keep the token of an earlier install,
# or create a new random one.

param(
    [int]$HttpPort = 5081,
    [int]$DiscoveryPort = 5082,
    [string]$InstallPath = "C:\ClubServer",
    [string]$DataPath = (Join-Path $env:ProgramData "PCClub"),
    [string]$PostgresConnectionString = "",
    [string]$EnrollmentToken = ""
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
Write-Host "  Project by Volodymyr Shabat" -ForegroundColor DarkGray
Write-Host ""

# Step 1: Create installation directory
Write-Host "[1/5] Creating installation directory..." -ForegroundColor Yellow
if (!(Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

$appsettings = Join-Path $InstallPath "appsettings.json"

# Keep the enrollment token of an earlier install: the club PCs were installed with it
if (-not $EnrollmentToken -and (Test-Path $appsettings)) {
    try {
        $previous = Get-Content $appsettings -Raw | ConvertFrom-Json
        if ($previous.Devices.EnrollmentToken) { $EnrollmentToken = $previous.Devices.EnrollmentToken }
    } catch {}
}
if (-not $EnrollmentToken) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $EnrollmentToken = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$serviceName = "ClubServer"
try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch {}
try { sc.exe delete $serviceName 2>$null | Out-Null } catch {}
Start-Sleep -Seconds 2

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

# Data directory: absolute path (a Windows service starts in C:\Windows\System32). The server copies a
# cms.db of an earlier version from there or from the install folder on the first start.
New-Item -ItemType Directory -Path $DataPath -Force | Out-Null
icacls $DataPath /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not restrict the permissions of $DataPath" }

$config = [ordered]@{
    Logging = @{
        LogLevel = @{
            Default = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
            "Microsoft.EntityFrameworkCore" = "Warning"
        }
    }
    Storage = @{ DataDirectory = $DataPath }
    Devices = @{
        EnrollmentToken = $EnrollmentToken
        AllowPendingRegistration = $true
    }
    Discovery = @{
        Enabled = $true
        Port = $DiscoveryPort
        HttpPort = $HttpPort
    }
    Urls = "http://0.0.0.0:$HttpPort"
}
if ($PostgresConnectionString) {
    $config.Database = @{ Provider = "postgres" }
    $config.ConnectionStrings = @{ DefaultConnection = $PostgresConnectionString }
} else {
    $config.Database = @{ Provider = "sqlite" }   # file: $DataPath\cms.db
}

$config | ConvertTo-Json -Depth 5 | Set-Content -Path $appsettings
# appsettings.json holds the enrollment token (and maybe a database password): admins and the service only
icacls $appsettings /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" | Out-Null

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
$exePath = Join-Path $InstallPath "Cms.Server.exe"

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
Write-Host "Data:       $DataPath" -ForegroundColor Cyan
Write-Host ""

# First start: no staff account yet -> the server writes a one-time setup code
$setupFile = Join-Path $DataPath "setup-code.txt"
for ($i = 0; $i -lt 30 -and !(Test-Path $setupFile); $i++) { Start-Sleep -Seconds 1 }
if (Test-Path $setupFile) {
    $setupCode = (Get-Content $setupFile -Raw).Trim()
    Write-Host "FIRST START: open http://localhost:$HttpPort and create the Owner account." -ForegroundColor Magenta
    Write-Host "  One-time setup code: $setupCode" -ForegroundColor Magenta
    Write-Host "  (also in $setupFile; it stops working when the Owner exists)" -ForegroundColor Gray
} else {
    Write-Host "Sign in at http://localhost:$HttpPort with a staff account." -ForegroundColor White
}
Write-Host ""
Write-Host "Install the club PCs with:" -ForegroundColor White
Write-Host "  ClubClientSetup.exe -ServerUrl `"http://<this-server-or-router>:$HttpPort`" -EnrollmentToken `"$EnrollmentToken`"" -ForegroundColor Cyan
Write-Host "  Keep the token secret. Without it a PC waits for approval in the admin UI (Devices)." -ForegroundColor Gray
Write-Host "Then set the staff PIN for the lock screens in the admin UI (Settings)." -ForegroundColor White
Write-Host "Firewall rules have been created for ports $HttpPort and $DiscoveryPort." -ForegroundColor White
Write-Host ""
