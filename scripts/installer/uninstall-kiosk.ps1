# Uninstall Kiosk Mode
# Reverses all changes made by install-client.ps1 -KioskMode
# Run as Administrator

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
Write-Host "  Kiosk Mode Uninstaller" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$kioskUser = "ClubUser"

# Step 1: Restore shell to explorer.exe
Write-Host "[1/5] Restoring shell to explorer.exe..." -ForegroundColor Yellow

# System-wide shell
$winlogonPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-ItemProperty -Path $winlogonPath -Name "Shell" -Value "explorer.exe"
Write-Host "   System shell restored to explorer.exe" -ForegroundColor Gray

# Per-user shell (if ClubUser profile exists)
$profilePath = "C:\Users\$kioskUser"
$ntUserDat = Join-Path $profilePath "NTUSER.DAT"
if (Test-Path $ntUserDat) {
    try {
        reg load "HKU\ClubUserHive" $ntUserDat 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $userShellKey = "Registry::HKEY_USERS\ClubUserHive\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
            if (Test-Path $userShellKey) {
                Remove-ItemProperty -Path $userShellKey -Name "Shell" -ErrorAction SilentlyContinue
            }
            [gc]::Collect()
            Start-Sleep -Seconds 1
            reg unload "HKU\ClubUserHive" 2>&1 | Out-Null
            Write-Host "   Per-user shell cleared" -ForegroundColor Gray
        }
    } catch {
        Write-Host "   Could not modify user hive (may already be clean)" -ForegroundColor Gray
    }
}

# Step 2: Disable auto-login
Write-Host "[2/5] Disabling auto-login..." -ForegroundColor Yellow
Remove-ItemProperty -Path $winlogonPath -Name "DefaultPassword" -ErrorAction SilentlyContinue
Set-ItemProperty -Path $winlogonPath -Name "AutoAdminLogon" -Value "0"
Remove-ItemProperty -Path $winlogonPath -Name "ForceAutoLogon" -ErrorAction SilentlyContinue
Write-Host "   Auto-login disabled" -ForegroundColor Gray

# Step 3: Remove Group Policy restrictions
Write-Host "[3/5] Removing security policies..." -ForegroundColor Yellow

# Re-enable Task Manager
$systemPolicyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
if (Test-Path $systemPolicyPath) {
    Remove-ItemProperty -Path $systemPolicyPath -Name "DisableTaskMgr" -ErrorAction SilentlyContinue
}
Write-Host "   Task Manager re-enabled" -ForegroundColor Gray

# Remove context menu restriction
$explorerPolicyPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer"
if (Test-Path $explorerPolicyPath) {
    Remove-ItemProperty -Path $explorerPolicyPath -Name "DisableContextMenusInStart" -ErrorAction SilentlyContinue
}
Write-Host "   Context menus restored" -ForegroundColor Gray

# Step 4: Remove ClubUser account (optional — prompt first)
Write-Host "[4/5] ClubUser account..." -ForegroundColor Yellow
$existingUser = Get-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue
if ($existingUser) {
    $response = Read-Host "   Delete '$kioskUser' account and profile? (y/N)"
    if ($response -eq 'y' -or $response -eq 'Y') {
        # Log off ClubUser first
        $sessions = query user 2>$null | Select-String $kioskUser
        if ($sessions) {
            $sessionId = ($sessions -split '\s+')[3]
            logoff $sessionId /f 2>$null
            Start-Sleep -Seconds 2
        }
        
        Remove-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue
        
        # Remove profile folder
        if (Test-Path $profilePath) {
            Remove-Item -Path $profilePath -Recurse -Force -ErrorAction SilentlyContinue
        }
        
        # Remove profile registry entry
        $profileListPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"
        Get-ChildItem $profileListPath | ForEach-Object {
            $profileImagePath = (Get-ItemProperty $_.PSPath).ProfileImagePath
            if ($profileImagePath -like "*$kioskUser*") {
                Remove-Item $_.PSPath -Recurse -Force
            }
        }
        
        Write-Host "   User '$kioskUser' removed" -ForegroundColor Gray
    } else {
        Write-Host "   User '$kioskUser' kept (you can delete manually later)" -ForegroundColor Gray
    }
} else {
    Write-Host "   User '$kioskUser' not found (already removed)" -ForegroundColor Gray
}

# Step 5: Clean up kiosk config and startup shortcuts
Write-Host "[5/5] Cleaning up..." -ForegroundColor Yellow

# Remove kiosk.json
$kioskConfigs = @("C:\ClubLauncher\kiosk.json")
foreach ($cfg in $kioskConfigs) {
    if (Test-Path $cfg) {
        Remove-Item $cfg -Force
        Write-Host "   Removed $cfg" -ForegroundColor Gray
    }
}

# Remove launcher startup shortcut
$startupFolder = [Environment]::GetFolderPath('CommonStartup')
$shortcutPath = Join-Path $startupFolder "ClubLauncher.lnk"
if (Test-Path $shortcutPath) {
    Remove-Item $shortcutPath -Force
    Write-Host "   Removed startup shortcut" -ForegroundColor Gray
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Kiosk Mode Removed!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Shell:       explorer.exe (restored)" -ForegroundColor Cyan
Write-Host "Auto-login:  Disabled" -ForegroundColor Cyan
Write-Host "Task Manager: Enabled" -ForegroundColor Cyan
Write-Host ""
Write-Host "Restart the PC for all changes to take effect." -ForegroundColor Yellow
Write-Host ""
