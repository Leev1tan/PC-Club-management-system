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

function New-InstallCommand {
    param(
        [string]$Path,
        [string]$ScriptName
    )

@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0$ScriptName" %*
if errorlevel 1 pause
"@ | Set-Content -Path $Path -Encoding ASCII
}

function Assert-NativeSuccess {
    param(
        [string]$Action
    )

    if ($LASTEXITCODE -ne 0) {
        throw "$Action failed with exit code $LASTEXITCODE"
    }
}

function New-BootstrapperPackage {
    param(
        [string]$PayloadZip,
        [string]$TargetExe,
        [string]$FriendlyName
    )

    $bootstrapper = Join-Path $repoRoot "tools\Installer.Bootstrapper"
    $payloadPath = Join-Path $bootstrapper "Payload.zip"
    $buildOut = Join-Path $outputDir ("bootstrapper-" + [IO.Path]::GetFileNameWithoutExtension($TargetExe))
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($TargetExe)

    Copy-Item $PayloadZip -Destination $payloadPath -Force
    if (Test-Path $buildOut) { Remove-Item $buildOut -Recurse -Force }

    dotnet publish $bootstrapper -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        "-p:AssemblyName=$assemblyName" `
        -o $buildOut | Out-Null
    Assert-NativeSuccess "Publishing $FriendlyName bootstrapper"

    $builtExe = Join-Path $buildOut ([IO.Path]::GetFileName($TargetExe))
    if (!(Test-Path $builtExe)) {
        throw "Failed to create setup executable: $TargetExe"
    }

    Copy-Item $builtExe -Destination $TargetExe -Force
    Remove-Item $buildOut -Recurse -Force
    Remove-Item $payloadPath -Force -ErrorAction SilentlyContinue
    Write-Host "   Created $FriendlyName" -ForegroundColor Gray
}

# Clean output directories
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Path $serverDir -Force | Out-Null
New-Item -ItemType Directory -Path $clientDir -Force | Out-Null

if (-not $SkipBuild) {
    # Build Server
    Write-Host "[1/5] Building Server..." -ForegroundColor Yellow
    dotnet publish Cms.Server -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$serverDir\server" | Out-Null
    Assert-NativeSuccess "Building Server"
    
    # Build Agent
    Write-Host "[2/5] Building Agent..." -ForegroundColor Yellow
    dotnet publish Cms.Agent.Service -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$clientDir\agent" | Out-Null
    Assert-NativeSuccess "Building Agent"
    
    # Build Launcher
    Write-Host "[3/5] Building Launcher..." -ForegroundColor Yellow
    dotnet publish Cms.Launcher -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -o "$clientDir\launcher" | Out-Null
    Assert-NativeSuccess "Building Launcher"

    # Build Admin UI
    Write-Host "[4/5] Building Admin UI..." -ForegroundColor Yellow
    Push-Location admin-ui
    try {
        if (!(Test-Path "node_modules")) {
            npm.cmd ci | Out-Null
            Assert-NativeSuccess "Installing Admin UI dependencies"
        }
        npm.cmd run build | Out-Null
        Assert-NativeSuccess "Building Admin UI"
    }
    finally {
        Pop-Location
    }

    $wwwroot = Join-Path "$serverDir\server" "wwwroot"
    New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
    Copy-Item -Path "admin-ui\out\*" -Destination $wwwroot -Recurse -Force
}

# Copy installer scripts
Write-Host "[5/5] Packaging installers..." -ForegroundColor Yellow

# Server package
Copy-Item "scripts\installer\install-server.ps1" -Destination $serverDir
New-InstallCommand -Path (Join-Path $serverDir "Install.cmd") -ScriptName "install-server.ps1"
@"
# Club Management System - Server Installation

Project by Volodymyr Shabat

## Quick Start
1. Extract this archive to a temporary folder
2. Open PowerShell as Administrator
3. Run: .\install-server.ps1
   Or run ClubServerSetup.exe as Administrator.

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
Copy-Item "scripts\installer\uninstall-kiosk.ps1" -Destination $clientDir
New-InstallCommand -Path (Join-Path $clientDir "Install.cmd") -ScriptName "install-client.ps1"
@"
# Club Management System - Client Installation

Project by Volodymyr Shabat

## Quick Start
1. Extract this archive to a temporary folder
2. Open PowerShell as Administrator
3. Run: .\install-client.ps1
   Or run ClubClientSetup.exe as Administrator.

## Options
- ``-ServerUrl "http://server:5081"`` (optional, auto-discovers if empty)
- ``-AgentPath "C:\ClubAgent"`` (default)
- ``-LauncherPath "C:\ClubLauncher"`` (default)
- ``-KioskMode`` (for full kiosk shell replacement, advanced)

## Isolated switch / MikroTik deployment
If client ports are isolated, do not rely on auto-discovery. Forward TCP 5081 on the router to the ClubServer PC and install clients with:

``ClubClientSetup.exe -ServerUrl "http://10.3.3.1:5081"``

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
$serverExe = Join-Path $outputDir "ClubServerSetup.exe"
$clientExe = Join-Path $outputDir "ClubClientSetup.exe"

Compress-Archive -Path "$serverDir\*" -DestinationPath $serverZip -Force
Compress-Archive -Path "$clientDir\*" -DestinationPath $clientZip -Force

New-BootstrapperPackage -PayloadZip $serverZip -TargetExe $serverExe -FriendlyName "Club Server Setup"
New-BootstrapperPackage -PayloadZip $clientZip -TargetExe $clientExe -FriendlyName "Club Client Setup"

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
Write-Host "Server EXE:       $serverExe" -ForegroundColor Cyan
Write-Host "Client EXE:       $clientExe" -ForegroundColor Cyan
Write-Host ""
Write-Host "Distribute these ZIP files to install:" -ForegroundColor White
Write-Host "  - server-installer.zip -> Club server PC" -ForegroundColor White
Write-Host "  - client-installer.zip -> Each gaming PC" -ForegroundColor White
Write-Host "  - ClubServerSetup.exe  -> Club server PC" -ForegroundColor White
Write-Host "  - ClubClientSetup.exe  -> Each gaming PC" -ForegroundColor White
Write-Host ""
