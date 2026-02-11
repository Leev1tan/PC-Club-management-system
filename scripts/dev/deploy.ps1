<#
.SYNOPSIS
    One-click build and deploy to VM.

.DESCRIPTION
    Builds Agent + Launcher, copies to VM via network share or shared folder,
    restarts the ClubAgent service, and verifies deployment.

.PARAMETER SkipBuild
    Skip the build step (use existing publish output)

.PARAMETER SkipService
    Don't restart the service (just copy files)

.PARAMETER Verify
    Run health check after deploy

.EXAMPLE
    .\deploy.ps1
    # Full deploy: build, copy, restart service

.EXAMPLE
    .\deploy.ps1 -SkipBuild -Verify
    # Copy existing build, restart, verify health
#>

param(
    [switch]$SkipBuild,
    [switch]$SkipService,
    [switch]$Verify,
    [switch]$LauncherOnly,
    [switch]$AgentOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root

# Load VM config
. "$PSScriptRoot\vm-config.ps1"
$vm = $script:VMConfig

Write-Host "`n=== CMS Deploy to VM ===" -ForegroundColor Cyan
Write-Host "Target: $($vm.VMIp)" -ForegroundColor Gray

# ============================================
# Step 1: Build
# ============================================
if (-not $SkipBuild) {
    Write-Host "`n[1/4] Building..." -ForegroundColor Yellow
    
    if (-not $LauncherOnly) {
        Write-Host "  Publishing Agent..." -ForegroundColor Gray
        dotnet publish .\Cms.Agent.Service\Cms.Agent.Service.csproj `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -o .\publish\agent\win-x64 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Agent build failed" }
        Write-Host "  Agent built." -ForegroundColor Green
    }
    
    if (-not $AgentOnly) {
        Write-Host "  Publishing Launcher..." -ForegroundColor Gray
        dotnet publish .\Cms.Launcher\Cms.Launcher.csproj `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -o .\publish\launcher\win-x64 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Launcher build failed" }
        Write-Host "  Launcher built." -ForegroundColor Green
    }
} else {
    Write-Host "`n[1/4] Skipping build (using existing)" -ForegroundColor DarkGray
}

# ============================================
# Step 2: Get Credentials & Test Connection
# ============================================
Write-Host "`n[2/4] Connecting to VM..." -ForegroundColor Yellow
$cred = Get-VMCredential

# Test WinRM connection
try {
    $session = New-PSSession -ComputerName $vm.VMIp -Credential $cred -ErrorAction Stop
    Write-Host "  Connected to $($vm.VMIp)" -ForegroundColor Green
} catch {
    Write-Host "  WinRM connection failed. Run vm-setup.ps1 on VM first." -ForegroundColor Red
    Write-Host "  Error: $_" -ForegroundColor DarkRed
    Pop-Location
    exit 1
}

# ============================================
# Step 3: Stop Service & Copy Files
# ============================================
Write-Host "`n[3/4] Deploying files..." -ForegroundColor Yellow

# Stop service before copy
if (-not $SkipService) {
    Invoke-Command -Session $session -ScriptBlock {
        try { Stop-Service ClubAgent -Force -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Seconds 1
    }
    Write-Host "  Service stopped" -ForegroundColor Gray
}

# Copy via network share (\\IP\C$\...)
$agentDest = "\\$($vm.VMIp)\C`$\$($vm.AgentPath.TrimStart('C:\'))"
$launcherDest = "\\$($vm.VMIp)\C`$\$($vm.LauncherPath.TrimStart('C:\'))"

# Ensure destination folders exist
Invoke-Command -Session $session -ScriptBlock {
    param($ap, $lp)
    New-Item -ItemType Directory -Path $ap -Force | Out-Null
    New-Item -ItemType Directory -Path $lp -Force | Out-Null
} -ArgumentList $vm.AgentPath, $vm.LauncherPath

if (-not $LauncherOnly) {
    Write-Host "  Copying Agent to $($vm.AgentPath)..." -ForegroundColor Gray
    Copy-Item -Path ".\publish\agent\win-x64\*" -Destination $agentDest -Force -Recurse
}

if (-not $AgentOnly) {
    Write-Host "  Copying Launcher to $($vm.LauncherPath)..." -ForegroundColor Gray
    Copy-Item -Path ".\publish\launcher\win-x64\*" -Destination $launcherDest -Force -Recurse
}

Write-Host "  Files copied." -ForegroundColor Green

# ============================================
# Step 4: Start Service & Verify
# ============================================
Write-Host "`n[4/4] Starting service..." -ForegroundColor Yellow

if (-not $SkipService) {
    # Ensure service exists and start it
    $result = Invoke-Command -Session $session -ScriptBlock {
        param($agentPath)
        $svc = Get-Service ClubAgent -ErrorAction SilentlyContinue
        if (-not $svc) {
            # Create service if doesn't exist
            $exePath = Join-Path $agentPath "Cms.Agent.Service.exe"
            sc.exe create ClubAgent binPath= $exePath start= auto obj= "NT AUTHORITY\LocalService" type= own | Out-Null
        }
        Start-Service ClubAgent
        Start-Sleep -Seconds 2
        $svc = Get-Service ClubAgent
        return @{ Status = $svc.Status.ToString() }
    } -ArgumentList $vm.AgentPath
    
    if ($result.Status -eq "Running") {
        Write-Host "  Service: Running" -ForegroundColor Green
    } else {
        Write-Host "  Service: $($result.Status)" -ForegroundColor Yellow
    }
}

# Cleanup session
Remove-PSSession $session

# ============================================
# Optional: Verify via API
# ============================================
if ($Verify) {
    Write-Host "`n[Verify] Checking device registration..." -ForegroundColor Cyan
    Start-Sleep -Seconds 5  # Wait for heartbeat
    
    $serverUrl = if ($vm.ServerUrl) { $vm.ServerUrl } else { "http://localhost:5081" }
    try {
        $devices = Invoke-RestMethod -Uri "$serverUrl/api/devices" -Method Get
        $vmDevice = $devices | Where-Object { $_.lastIp -match $vm.VMIp.Split('.')[3] }
        if ($vmDevice) {
            Write-Host "  Device registered: $($vmDevice.hostname) (Status: $($vmDevice.status))" -ForegroundColor Green
        } else {
            Write-Host "  Device not found yet. Check Admin UI." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  Could not verify (server may not be running): $_" -ForegroundColor Yellow
    }
}

Pop-Location
Write-Host "`n=== Deploy Complete ===" -ForegroundColor Cyan
Write-Host "Launcher: $($vm.LauncherPath)\Cms.Launcher.exe" -ForegroundColor Gray
