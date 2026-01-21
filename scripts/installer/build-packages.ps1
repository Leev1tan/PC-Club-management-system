# Build Installer Packages
# Creates self-contained installer packages for server and client
# Output: publish/installers/server-installer.zip, publish/installers/client-installer.zip

param(
    [switch]$SkipBuild = $false
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Item $PSScriptRoot).Parent.Parent.FullName
Set-Location $repoRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Building Installer Packages" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$outputDir = Join-Path $repoRoot "publish\installers"
$serverDir = Join-Path $outputDir "server-package"
$clientDir = Join-Path $outputDir "client-package"

# Clean output directories
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Path $serverDir -Force | Out-Null
New-Item -ItemType Directory -Path $clientDir -Force | Out-Null

if (-not $SkipBuild) {
    # Build Server
    Write-Host "[1/4] Building Server..." -ForegroundColor Yellow
    dotnet publish Cms.Server -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$serverDir\server" | Out-Null
    
    # Build Agent
    Write-Host "[2/4] Building Agent..." -ForegroundColor Yellow
    dotnet publish Cms.Agent.Service -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$clientDir\agent" | Out-Null
    
    # Build Launcher
    Write-Host "[3/4] Building Launcher..." -ForegroundColor Yellow
    dotnet publish Cms.Launcher -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -o "$clientDir\launcher" | Out-Null
}

# Copy installer scripts
Write-Host "[4/4] Packaging installers..." -ForegroundColor Yellow

# Server package
Copy-Item "scripts\installer\install-server.ps1" -Destination $serverDir
@"
# Club Management System - Server Installation

## Quick Start
1. Extract this archive to a temporary folder
2. Open PowerShell as Administrator
3. Run: .\install-server.ps1

## Options
- ``-HttpPort 5081`` (default: 5081)
- ``-DiscoveryPort 5082`` (default: 5082)
- ``-InstallPath "C:\ClubServer"`` (default)
- ``-PostgresConnectionString "Host=..."`` (optional, uses embedded SQLite otherwise)

## After Installation
- Server API: http://localhost:5081
- Swagger docs: http://localhost:5081/swagger
- Windows Service: ClubServer

Firewall rules are automatically created for ports 5081 (TCP) and 5082 (UDP).
"@ | Set-Content "$serverDir\README.txt"

# Client package
Copy-Item "scripts\installer\install-client.ps1" -Destination $clientDir
@"
# Club Management System - Client Installation

## Quick Start
1. Extract this archive to a temporary folder
2. Open PowerShell as Administrator
3. Run: .\install-client.ps1

## Options
- ``-ServerUrl "http://server:5081"`` (optional, auto-discovers if empty)
- ``-AgentPath "C:\ClubAgent"`` (default)
- ``-LauncherPath "C:\ClubLauncher"`` (default)
- ``-KioskMode`` (for full kiosk shell replacement, advanced)

## Auto-Discovery
By default, the agent will broadcast on the LAN to find the server.
Make sure UDP port 5082 is open on both server and client.

## After Installation
- Agent runs as Windows Service: ClubAgent
- Launcher starts on user login
- Staff unlock: Ctrl+Shift+U (PIN: 1234)
"@ | Set-Content "$clientDir\README.txt"

# Create ZIP archives
Write-Host ""
Write-Host "Creating ZIP archives..." -ForegroundColor Yellow

$serverZip = Join-Path $outputDir "server-installer.zip"
$clientZip = Join-Path $outputDir "client-installer.zip"

Compress-Archive -Path "$serverDir\*" -DestinationPath $serverZip -Force
Compress-Archive -Path "$clientDir\*" -DestinationPath $clientZip -Force

# Cleanup
Remove-Item $serverDir -Recurse -Force
Remove-Item $clientDir -Recurse -Force

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Server Installer: $serverZip" -ForegroundColor Cyan
Write-Host "Client Installer: $clientZip" -ForegroundColor Cyan
Write-Host ""
Write-Host "Distribute these ZIP files to install:" -ForegroundColor White
Write-Host "  - server-installer.zip -> Club server PC" -ForegroundColor White
Write-Host "  - client-installer.zip -> Each gaming PC" -ForegroundColor White
Write-Host ""
