# Uninstall Kiosk Mode
# Reverses all changes made by install-client.ps1 -KioskMode (also the machine-wide values of older versions)
# Run as Administrator, or as SYSTEM. Without a console it keeps the ClubUser account (no question).

$ErrorActionPreference = 'Stop'

function Test-IsAdmin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# False for SYSTEM, a service, -NonInteractive or redirected input: then nobody can answer Read-Host.
function Test-InteractiveSession {
    if (-not [Environment]::UserInteractive) { return $false }
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { return $false }
    foreach ($arg in [Environment]::GetCommandLineArgs()) {
        if ($arg -like '-NonI*') { return $false }
    }
    try { if ([Console]::IsInputRedirected) { return $false } } catch { }
    return $true
}

# reg.exe with its exit code (Windows PowerShell 5.1 turns its error text into an exception under 'Stop').
function Invoke-Reg([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $text = (& reg.exe @Arguments 2>&1 | Out-String).Trim()
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = $text }
}

# Deletes the LSA secret "DefaultPassword" (the auto-login password of install-client.ps1). C# 5 only.
$lsaSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClubUninstall
{
    public static class AutoLogonSecret
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LsaUnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LsaObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [DllImport("advapi32.dll")]
        private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

        [DllImport("advapi32.dll")]
        private static extern uint LsaStorePrivateData(IntPtr policyHandle, ref LsaUnicodeString keyName, IntPtr privateData);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr policyHandle);

        [DllImport("advapi32.dll")]
        private static extern int LsaNtStatusToWinError(uint status);

        private const uint PolicyCreateSecret = 0x00000020;
        private const uint StatusObjectNameNotFound = 0xC0000034;
        private const string SecretName = "DefaultPassword";

        // True when a secret was deleted, false when there was none
        public static bool Delete()
        {
            LsaObjectAttributes attributes = new LsaObjectAttributes();
            IntPtr policy;
            uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateSecret, out policy);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
            IntPtr name = Marshal.StringToHGlobalUni(SecretName);
            try
            {
                LsaUnicodeString key = new LsaUnicodeString();
                key.Buffer = name;
                key.Length = (ushort)(SecretName.Length * 2);
                key.MaximumLength = (ushort)(key.Length + 2);
                status = LsaStorePrivateData(policy, ref key, IntPtr.Zero);
                if (status == StatusObjectNameNotFound) return false;
                if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                LsaClose(policy);
            }
        }
    }
}
'@

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

# Per-user shell and policies (in the hive of ClubUser: HKU\<SID> while ClubUser is signed in, else its NTUSER.DAT)
$existingUser = Get-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue
$profilePath = "C:\Users\$kioskUser"
$hiveRoot = $null
$hiveLoaded = $false
if ($existingUser) {
    $sid = $existingUser.SID.Value
    $profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
    $imagePath = (Get-ItemProperty -Path $profileKey -Name 'ProfileImagePath' -ErrorAction SilentlyContinue).ProfileImagePath
    if ($imagePath) { $profilePath = [Environment]::ExpandEnvironmentVariables($imagePath) }
    if (Test-Path "Registry::HKEY_USERS\$sid") { $hiveRoot = "Registry::HKEY_USERS\$sid" }
}
$ntUserDat = Join-Path $profilePath "NTUSER.DAT"
if (-not $hiveRoot -and (Test-Path $ntUserDat)) {
    if ((Invoke-Reg @('load', 'HKU\ClubUserHive', $ntUserDat)).ExitCode -eq 0) {
        $hiveRoot = "Registry::HKEY_USERS\ClubUserHive"
        $hiveLoaded = $true
    }
}
if ($hiveRoot) {
    $userValues = @(
        @("$hiveRoot\Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell"),
        @("$hiveRoot\Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr"),
        @("$hiveRoot\Software\Policies\Microsoft\Windows\Explorer", "DisableContextMenusInStart")
    )
    foreach ($value in $userValues) {
        if (Test-Path $value[0]) { Remove-ItemProperty -Path $value[0] -Name $value[1] -ErrorAction SilentlyContinue }
    }
    if ($hiveLoaded) {
        $unloaded = $false
        for ($i = 0; $i -lt 5 -and -not $unloaded; $i++) {
            [gc]::Collect()
            [gc]::WaitForPendingFinalizers()
            $unloaded = (Invoke-Reg @('unload', 'HKU\ClubUserHive')).ExitCode -eq 0
            if (-not $unloaded) { Start-Sleep -Seconds 1 }
        }
        if (-not $unloaded) { Write-Warning "Could not unload HKU\ClubUserHive. Restart the PC before ClubUser signs in." }
    }
    Write-Host "   Per-user shell and policies of $kioskUser cleared" -ForegroundColor Gray
} else {
    Write-Host "   Could not open the registry hive of $kioskUser (may already be clean)" -ForegroundColor Gray
}

# Step 2: Disable auto-login
Write-Host "[2/5] Disabling auto-login..." -ForegroundColor Yellow
Remove-ItemProperty -Path $winlogonPath -Name "DefaultPassword" -ErrorAction SilentlyContinue
Set-ItemProperty -Path $winlogonPath -Name "AutoAdminLogon" -Value "0"
Remove-ItemProperty -Path $winlogonPath -Name "ForceAutoLogon" -ErrorAction SilentlyContinue
try {
    if (-not ('ClubUninstall.AutoLogonSecret' -as [type])) { Add-Type -TypeDefinition $lsaSource -IgnoreWarnings }
    if ([ClubUninstall.AutoLogonSecret]::Delete()) { Write-Host "   Auto-login password (LSA secret) deleted" -ForegroundColor Gray }
} catch {
    Write-Warning "Could not delete the LSA secret DefaultPassword: $($_.Exception.Message)"
}
Write-Host "   Auto-login disabled" -ForegroundColor Gray

# Step 3: Remove Group Policy restrictions
Write-Host "[3/5] Removing security policies..." -ForegroundColor Yellow

# Re-enable Task Manager (the machine-wide value of older installers)
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

# Step 4: Remove ClubUser account (optional - prompt first; without a console the account stays)
Write-Host "[4/5] ClubUser account..." -ForegroundColor Yellow
if ($existingUser) {
    $response = if (Test-InteractiveSession) { Read-Host "   Delete '$kioskUser' account and profile? (y/N)" } else { 'n' }
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

# Remove the scheduled task that starts the watchdog of the lock screen (install-client.ps1 -KioskMode)
if (Get-ScheduledTask -TaskName 'ClubLauncherWatchdog' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName 'ClubLauncherWatchdog' -Confirm:$false
    Write-Host "   Removed the scheduled task ClubLauncherWatchdog" -ForegroundColor Gray
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
