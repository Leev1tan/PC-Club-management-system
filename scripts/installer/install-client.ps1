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

# Step 6: Configure Launcher autostart
Write-Host "[6/8] Configuring Launcher autostart..." -ForegroundColor Yellow
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
    
    Write-Host "   Launcher added to common startup" -ForegroundColor Gray
}

# Step 7: Kiosk Mode setup (if requested)
if ($KioskMode) {
    Write-Host "[7/8] Setting up Kiosk Mode..." -ForegroundColor Yellow
    
    $kioskUser = "ClubUser"
    $kioskPassword = "ClubKiosk2024!" # Internal only, user never types this
    
    # 7a: Create restricted ClubUser account
    Write-Host "   Creating '$kioskUser' account..." -ForegroundColor Gray
    try {
        $existingUser = Get-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue
        if ($existingUser) {
            Write-Host "   User '$kioskUser' already exists, updating..." -ForegroundColor Gray
            Set-LocalUser -Name $kioskUser -Password (ConvertTo-SecureString $kioskPassword -AsPlainText -Force) -PasswordNeverExpires $true
        } else {
            New-LocalUser -Name $kioskUser `
                -Password (ConvertTo-SecureString $kioskPassword -AsPlainText -Force) `
                -FullName "Club Gaming Station" `
                -Description "Restricted kiosk account for PC Club" `
                -PasswordNeverExpires `
                -UserMayNotChangePassword | Out-Null
        }
        
        # Add to Users group (standard, not admin)
        try { Add-LocalGroupMember -Group "Users" -Member $kioskUser -ErrorAction SilentlyContinue } catch {}
        
        # Remove from any admin groups
        try { Remove-LocalGroupMember -Group "Administrators" -Member $kioskUser -ErrorAction SilentlyContinue } catch {}
        
        Write-Host "   Account '$kioskUser' ready (restricted, non-admin)" -ForegroundColor Gray
    } catch {
        Write-Error "Failed to create kiosk user: $_"
        exit 1
    }
    
    # 7b: Set auto-login for ClubUser
    Write-Host "   Configuring auto-login..." -ForegroundColor Gray
    $winlogonPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    Set-ItemProperty -Path $winlogonPath -Name "DefaultUserName" -Value $kioskUser
    Set-ItemProperty -Path $winlogonPath -Name "DefaultPassword" -Value $kioskPassword
    Set-ItemProperty -Path $winlogonPath -Name "AutoAdminLogon" -Value "1"
    Set-ItemProperty -Path $winlogonPath -Name "ForceAutoLogon" -Value "1"
    Write-Host "   Auto-login enabled for '$kioskUser'" -ForegroundColor Gray
    
    # 7c: Set Launcher as shell for ClubUser (per-user, not system-wide)
    Write-Host "   Setting Launcher as shell..." -ForegroundColor Gray
    
    # Load ClubUser's registry hive
    $profilePath = "C:\Users\$kioskUser"
    if (!(Test-Path $profilePath)) {
        # Force profile creation by loading user profile
        $securePass = ConvertTo-SecureString $kioskPassword -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential($kioskUser, $securePass)
        Start-Process -FilePath "cmd.exe" -ArgumentList "/c echo profile_created" -Credential $cred -Wait -NoNewWindow -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
    
    $ntUserDat = Join-Path $profilePath "NTUSER.DAT"
    if (Test-Path $ntUserDat) {
        # Load the user's registry hive
        $regLoad = reg load "HKU\ClubUserHive" $ntUserDat 2>&1
        if ($LASTEXITCODE -eq 0) {
            # Set shell replacement for this user only
            $userShellPath = "HKU:\ClubUserHive\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
            if (!(Test-Path "Registry::HKEY_USERS\ClubUserHive\Software\Microsoft\Windows NT\CurrentVersion\Winlogon")) {
                New-Item -Path "Registry::HKEY_USERS\ClubUserHive\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" -Force | Out-Null
            }
            Set-ItemProperty -Path "Registry::HKEY_USERS\ClubUserHive\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "Shell" -Value "`"$launcherExe`""
            
            # Unload the hive
            [gc]::Collect()
            Start-Sleep -Seconds 1
            reg unload "HKU\ClubUserHive" 2>&1 | Out-Null
            
            Write-Host "   Shell set to Launcher (per-user)" -ForegroundColor Gray
        } else {
            # Fallback: set system-wide but only if user hive not accessible
            Write-Host "   Could not load user hive, setting system-wide shell..." -ForegroundColor Yellow
            Set-ItemProperty -Path $winlogonPath -Name "Shell" -Value "`"$launcherExe`""
            Write-Host "   Shell set to Launcher (system-wide)" -ForegroundColor Yellow
        }
    } else {
        # Profile doesn't exist yet — set system-wide shell for Winlogon
        Write-Host "   User profile not found, setting system-wide shell..." -ForegroundColor Yellow
        Set-ItemProperty -Path $winlogonPath -Name "Shell" -Value "`"$launcherExe`""
        Write-Host "   Shell set to Launcher (system-wide)" -ForegroundColor Yellow
    }
    
    # 7d: Group Policy lockdown
    Write-Host "[8/8] Applying security policies..." -ForegroundColor Yellow
    
    # Disable Task Manager
    $explorerPolicyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    if (!(Test-Path $explorerPolicyPath)) { New-Item -Path $explorerPolicyPath -Force | Out-Null }
    Set-ItemProperty -Path $explorerPolicyPath -Name "DisableTaskMgr" -Value 1 -Type DWord
    Write-Host "   Task Manager disabled" -ForegroundColor Gray
    
    # Disable right-click on desktop / context menu
    $shellPolicyPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer"
    if (!(Test-Path $shellPolicyPath)) { New-Item -Path $shellPolicyPath -Force | Out-Null }
    Set-ItemProperty -Path $shellPolicyPath -Name "DisableContextMenusInStart" -Value 1 -Type DWord
    Write-Host "   Context menus restricted" -ForegroundColor Gray
    
    # Disable Windows key (via keyboard filter or registry)
    $keyboardPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Keyboard Layout"
    # Note: Full keyboard filtering requires Embedded Shell Launcher or keyboard filter feature
    Write-Host "   Keyboard hooks will be managed by Launcher" -ForegroundColor Gray
    
    # Write kiosk config marker for Launcher to detect
    $kioskConfig = Join-Path $LauncherPath "kiosk.json"
    @{
        KioskMode = $true
        KioskUser = $kioskUser
        BlockAltTab = $true
        BlockAltF4 = $true
        BlockWinKey = $true
        StaffUnlockPin = "1234"
        InstalledUtc = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json | Set-Content -Path $kioskConfig
    Write-Host "   Kiosk config written to $kioskConfig" -ForegroundColor Gray
    
} else {
    Write-Host "[7/8] Kiosk mode: Skipped (use -KioskMode to enable)" -ForegroundColor Gray
    Write-Host "[8/8] Security policies: Skipped" -ForegroundColor Gray
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Client Installation Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Agent Service: Running (ClubAgent)" -ForegroundColor Cyan
Write-Host "Launcher: Installed at $LauncherPath" -ForegroundColor Cyan

if ($KioskMode) {
    Write-Host ""
    Write-Host "KIOSK MODE ENABLED:" -ForegroundColor Magenta
    Write-Host "  - User: ClubUser (auto-login on boot)" -ForegroundColor White
    Write-Host "  - Shell: Cms.Launcher.exe (replaces explorer)" -ForegroundColor White
    Write-Host "  - Staff unlock: Ctrl+Shift+U (PIN: 1234)" -ForegroundColor White
    Write-Host "  - Switch to admin: Ctrl+Alt+Del > Switch User" -ForegroundColor White
    Write-Host ""
    Write-Host "To undo kiosk mode, run: uninstall-kiosk.ps1" -ForegroundColor Yellow
}

Write-Host ""
if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    Write-Host "The agent will auto-discover the server on your LAN." -ForegroundColor White
} else {
    Write-Host "Agent connects to: $ServerUrl" -ForegroundColor White
}
Write-Host ""
Write-Host "Restart the PC for all changes to take effect." -ForegroundColor Yellow
Write-Host ""
