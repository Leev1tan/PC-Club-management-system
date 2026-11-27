# Client (Gaming PC) Installer Script for Windows
# Run as Administrator, or as SYSTEM (RMM tools, Intune, a VM guest agent): no step needs a console.
# Usage: .\install-client.ps1 -ServerUrl "http://server:5081" [-EnrollmentToken "<token>"] [-KioskMode] [-AdminPassword <SecureString>]
#
# -EnrollmentToken: the server's CMS_ENROLLMENT_TOKEN / Devices:EnrollmentToken. With it the PC is
#   approved at once; without it the PC waits as "pending" until staff approve it in the admin UI.
#   The token is used only with an explicit -ServerUrl (never sent to a server found by discovery),
#   is stored in C:\ProgramData\ClubAgent\private (SYSTEM, Administrators, agent only) and is
#   deleted by the agent after its first successful registration or heartbeat. On a PC that is registered
#   already (an update) the installer does not store it, and removes a token file of an earlier run.
# -KioskMode: the player account ClubUser signs in automatically and gets the lock screen as its shell.
#   - Its password is random (32 characters) and new on every run. The installer never prints it and never
#     writes it to a file. Windows keeps it for the automatic sign-in as the LSA secret "DefaultPassword"
#     (not as the registry value DefaultPassword, which local users can read). To reset it, run the
#     installer again with the same options.
#   - The shell, "Task Manager off" and the other lockdown policies go into the registry hive of ClubUser
#     only. ClubAdmin and the other administrators keep explorer.exe and Task Manager.
# -AdminPassword: the password of the recovery account ClubAdmin (Administrators) when the installer must
#   create it. Without it the installer asks in an interactive console. In a run without a console (SYSTEM,
#   -NonInteractive) it creates no ClubAdmin and prints a warning. From PowerShell, for example:
#     .\install-client.ps1 -ServerUrl "http://server:5081" -KioskMode -AdminPassword (Read-Host -AsSecureString)

param(
    [string]$ServerUrl = "",  # Empty = auto-discovery
    [string]$EnrollmentToken = "",
    [string]$AgentPath = "C:\ClubAgent",
    [string]$LauncherPath = "C:\ClubLauncher",
    [switch]$KioskMode = $false,
    [System.Security.SecureString]$AdminPassword = $null
)

$ErrorActionPreference = 'Stop'

function Test-IsAdmin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Stops the install with a clear message and exit code 1 (RMM tools and Intune report the exit code).
# A lock screen that this run stopped must not stay away: the watchdog task is switched on again and the sessions of the
# kiosk user are signed out, so the automatic sign-in starts the lock screen (Resume-LockScreens).
function Stop-Install([string]$Message) {
    if (Get-Command Resume-LockScreens -ErrorAction SilentlyContinue) { Resume-LockScreens }
    Write-Host ""
    Write-Host "ERROR: $Message" -ForegroundColor Red
    Write-Host "RESULT: FAILED (exit code 1)" -ForegroundColor Red
    exit 1
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

# reg.exe with its exit code and text. Windows PowerShell 5.1 turns the error text of a native program into an
# exception when $ErrorActionPreference is 'Stop'; here the exit code decides.
function Invoke-Reg([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $text = (& reg.exe @Arguments 2>&1 | Out-String).Trim()
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = $text }
}

# userenv!CreateProfile (the profile of ClubUser, also as SYSTEM) and the LSA secret of the automatic sign-in.
# C# 5 only: Windows PowerShell 5.1 compiles this with the C# compiler of .NET Framework.
$kioskNativeSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace ClubInstall
{
    public static class KioskNative
    {
        // HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS): the profile exists
        public const int ProfileExists = unchecked((int)0x800700B7);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int CreateProfile(string pszUserSid, string pszUserName, StringBuilder pszProfilePath, uint cchProfilePath);

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

        // Stores the password as the LSA secret "DefaultPassword": Winlogon uses it for the automatic sign-in.
        // null deletes the secret. The password is in unmanaged memory only for the call and is zeroed after it.
        public static void SetAutoLogonPassword(SecureString password)
        {
            LsaObjectAttributes attributes = new LsaObjectAttributes();
            IntPtr policy;
            uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateSecret, out policy);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
            IntPtr name = Marshal.StringToHGlobalUni(SecretName);
            IntPtr secret = IntPtr.Zero;
            IntPtr data = IntPtr.Zero;
            try
            {
                LsaUnicodeString key = new LsaUnicodeString();
                key.Buffer = name;
                key.Length = (ushort)(SecretName.Length * 2);
                key.MaximumLength = (ushort)(key.Length + 2);
                if (password != null)
                {
                    secret = Marshal.SecureStringToGlobalAllocUnicode(password);
                    LsaUnicodeString value = new LsaUnicodeString();
                    value.Buffer = secret;
                    value.Length = (ushort)(password.Length * 2);
                    value.MaximumLength = (ushort)(value.Length + 2);
                    data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LsaUnicodeString)));
                    Marshal.StructureToPtr(value, data, false);
                }
                status = LsaStorePrivateData(policy, ref key, data);
                if (status != 0 && !(password == null && status == StatusObjectNameNotFound))
                    throw new Win32Exception(LsaNtStatusToWinError(status));
            }
            finally
            {
                if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
                if (secret != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(secret);
                Marshal.FreeHGlobal(name);
                LsaClose(policy);
            }
        }
    }
}
'@

# A new random password for ClubUser: 32 characters from a cryptographic random generator, with upper-case and
# lower-case letters, digits and symbols, so every password policy accepts it. It exists only in memory as a
# SecureString: nobody types it, the installer never prints it and never writes it to a file.
function New-KioskPassword {
    $sets = @('ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!#%+-=?@_~')
    $all = -join $sets
    $length = 32
    $limit = 256 - (256 % $all.Length)   # rejection sampling: every character is equally likely
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $byte = New-Object byte[] 1
    $chars = New-Object char[] $length
    try {
        while ($true) {
            $i = 0
            while ($i -lt $length) {
                $rng.GetBytes($byte)
                if ($byte[0] -lt $limit) {
                    $chars[$i] = $all[$byte[0] % $all.Length]
                    $i++
                }
            }
            $complete = $true
            foreach ($set in $sets) {
                $found = $false
                foreach ($c in $chars) { if ($set.IndexOf($c) -ge 0) { $found = $true; break } }
                if (-not $found) { $complete = $false }
            }
            if ($complete) { break }
        }
        $password = New-Object Security.SecureString
        foreach ($c in $chars) { $password.AppendChar($c) }
        $password.MakeReadOnly()
        return $password
    } finally {
        [Array]::Clear($chars, 0, $chars.Length)
        $rng.Dispose()
    }
}

# The profile folder of a user (ProfileList in the registry); $null when Windows has no profile for the SID.
function Get-ProfilePath([string]$Sid) {
    $key = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid"
    if (!(Test-Path $key)) { return $null }
    $path = (Get-ItemProperty -Path $key -Name 'ProfileImagePath' -ErrorAction SilentlyContinue).ProfileImagePath
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    return [Environment]::ExpandEnvironmentVariables($path)
}

# The profile of the kiosk user (with NTUSER.DAT) must exist before its shell and policies go into its hive.
# userenv!CreateProfile works also as SYSTEM. A sign-in with Start-Process -Credential (the old way) gives
# "Access is denied" as SYSTEM, so it is only the fallback. Returns the profile folder, or $null.
function New-KioskProfile([string]$UserName, [string]$Sid, [Security.SecureString]$Password) {
    $buffer = New-Object System.Text.StringBuilder 260
    $hr = [ClubInstall.KioskNative]::CreateProfile($Sid, $UserName, $buffer, [uint32]$buffer.Capacity)
    if ($hr -eq 0) {
        Write-Host "   Profile of '$UserName' created: $($buffer.ToString())" -ForegroundColor Gray
    } elseif ($hr -eq [ClubInstall.KioskNative]::ProfileExists) {
        Write-Host "   Profile of '$UserName' exists" -ForegroundColor Gray
    } else {
        Write-Warning ("CreateProfile failed (HRESULT 0x{0:X8}); trying a sign-in of '{1}' with Start-Process" -f $hr, $UserName)
        try {
            $credential = New-Object System.Management.Automation.PSCredential($UserName, $Password)
            Start-Process -FilePath "cmd.exe" -ArgumentList "/c exit 0" -Credential $credential -LoadUserProfile -Wait -NoNewWindow
            Start-Sleep -Seconds 3
        } catch {
            Write-Warning "Start-Process as '$UserName' failed: $($_.Exception.Message)"
        }
    }
    $path = Get-ProfilePath $Sid
    if ($path -and (Test-Path -LiteralPath (Join-Path $path 'NTUSER.DAT'))) { return $path }
    return $null
}

# The registry hive of the kiosk user: HKU\<SID> when the user is signed in (Windows has it loaded), else its
# NTUSER.DAT loaded at HKU\ClubUserHive. $null when it cannot be loaded.
$kioskHiveName = 'ClubUserHive'
function Mount-KioskHive([string]$Sid, [string]$ProfilePath) {
    if (Test-Path "Registry::HKEY_USERS\$Sid") {
        return [pscustomobject]@{ Root = "Registry::HKEY_USERS\$Sid"; Loaded = $false }
    }
    if (Test-Path "Registry::HKEY_USERS\$kioskHiveName") {
        Invoke-Reg @('unload', "HKU\$kioskHiveName") | Out-Null   # left loaded by an interrupted run
    }
    $result = Invoke-Reg @('load', "HKU\$kioskHiveName", (Join-Path $ProfilePath 'NTUSER.DAT'))
    if ($result.ExitCode -ne 0) {
        Write-Host "   reg load: $($result.Text)" -ForegroundColor Red
        return $null
    }
    return [pscustomobject]@{ Root = "Registry::HKEY_USERS\$kioskHiveName"; Loaded = $true }
}

# Unloads a hive that Mount-KioskHive loaded. A hive that stays loaded blocks the next sign-in of the user.
function Dismount-KioskHive($Hive) {
    if ($null -eq $Hive -or -not $Hive.Loaded) { return }
    for ($i = 0; $i -lt 5; $i++) {
        # The registry provider keeps key handles open until the garbage collector closes them
        [gc]::Collect()
        [gc]::WaitForPendingFinalizers()
        if ((Invoke-Reg @('unload', "HKU\$kioskHiveName")).ExitCode -eq 0) { return }
        Start-Sleep -Seconds 1
    }
    Write-Warning "Could not unload HKU\$kioskHiveName. Restart the PC before ClubUser signs in."
}

# The shell and the lockdown policies of the kiosk user, in its own hive only (never machine-wide).
# The shell is the watchdog of the lock screen ("Cms.Launcher.exe --watchdog", no window): it starts the lock screen and
# starts it again within about a second when it ends (a player killed it, or it failed).
function Set-KioskUserPolicies([string]$Root, [string]$LauncherExe) {
    $winlogon = "$Root\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $system = "$Root\Software\Microsoft\Windows\CurrentVersion\Policies\System"
    $explorer = "$Root\Software\Policies\Microsoft\Windows\Explorer"
    foreach ($key in @($winlogon, $system, $explorer)) {
        if (!(Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    }
    New-ItemProperty -Path $winlogon -Name 'Shell' -Value "`"$LauncherExe`" --watchdog" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $system -Name 'DisableTaskMgr' -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $explorer -Name 'DisableContextMenusInStart' -Value 1 -PropertyType DWord -Force | Out-Null
}

# The third guard of the lock screen: a scheduled task of the kiosk user starts "Cms.Launcher.exe --watchdog" at its
# sign-in and every minute. The watchdog ends at once while one runs in the session; when a player kills the watchdog
# and the lock screen together, the task starts them again within a minute. Logon type Interactive: no password is
# stored, and the task runs only while the user is signed in. A failure is a warning (the shell watchdog still works).
function Register-LockScreenTask([string]$UserName, [string]$LauncherExe) {
    try {
        $user = "$env:COMPUTERNAME\$UserName"
        $action = New-ScheduledTaskAction -Execute $LauncherExe -Argument '--watchdog' -WorkingDirectory (Split-Path -Parent $LauncherExe)
        $atLogon = New-ScheduledTaskTrigger -AtLogOn -User $user
        $everyMinute = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 1)
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
            -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 0)
        Register-ScheduledTask -TaskName 'ClubLauncherWatchdog' -Action $action -Trigger @($atLogon, $everyMinute) `
            -Principal $principal -Settings $settings -Force | Out-Null
        Write-Host "   Scheduled task ClubLauncherWatchdog: starts the lock screen watchdog of '$UserName' every minute" -ForegroundColor Gray
    } catch {
        Write-Warning "Could not register the scheduled task ClubLauncherWatchdog: $($_.Exception.Message). The shell watchdog still restarts the lock screen."
    }
}

# Restart actions of a service: when it ends with an error, Windows starts it again after 5 s, 5 s, then 30 s (the
# count goes back to zero after one day). failureflag 1: also when the service stops itself with an error code.
function Set-ServiceRecovery([string]$Name) {
    sc.exe failure $Name reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set the restart actions of the service $Name (sc.exe failure)" }
    sc.exe failureflag $Name 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set the failure flag of the service $Name (sc.exe failureflag)" }
}

# Removes a machine-wide value that an older installer wrote (it applied to every account, also ClubAdmin).
function Remove-MachineValue([string]$Path, [string]$Name, [string]$What) {
    if ($null -ne (Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -Path $Path -Name $Name
        Write-Host "   Removed the machine-wide $What of an older install" -ForegroundColor Gray
    }
}

# --- Update helpers (Windows test 2026-11-27, defect 4: an update stopped the agent, then could not replace the
# --- lock screen of the signed-in ClubUser and left the PC out of service). The order is: prepare everything,
# --- stop the agent and the lock screen, copy, start; on a failure the old version is restored and started again.

# Copies a folder with robocopy: every file (also unchanged ones), 3 tries 2 s apart for a file that is held for a
# moment (antivirus). -Mirror also removes files that the source does not have (used to restore the old version).
# True when every file was copied (robocopy exit code below 8).
function Copy-Folder([string]$From, [string]$To, [switch]$Mirror) {
    $ErrorActionPreference = 'Continue'
    $mode = '/E'
    if ($Mirror) { $mode = '/MIR' }
    & robocopy.exe $From $To $mode /IS /IT /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    return ($LASTEXITCODE -lt 8)
}

# A new empty folder that only SYSTEM and Administrators may change (the agent and the players may read it): the staged
# new version and the backup of the old one, so a player cannot plant a program there.
function New-ProtectedFolder([string]$Path) {
    if (Test-Path $Path) { Remove-Item -Path $Path -Recurse -Force }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    icacls $Path /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)RX" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set the permissions of $Path" }
    if (Get-ChildItem -Path $Path -Force) { throw "$Path is not empty after it was created" }
}

# The agent settings: the shipped appsettings.json with the server URL of this run, and the parts that an administrator
# may have changed on this PC (Teardown, AccountPool) kept from the earlier install.
function Set-AgentConfig([string]$Path, $PreviousConfig) {
    try {
        $config = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    } catch {
        $config = [pscustomobject]@{ Logging = @{ LogLevel = @{ Default = "Information"; "Microsoft.Hosting.Lifetime" = "Information" } } }
    }
    $config | Add-Member -NotePropertyName Server -NotePropertyValue @{ BaseUrl = $ServerUrl } -Force  # Empty = auto-discovery
    foreach ($section in @('Teardown', 'AccountPool')) {
        if ($PreviousConfig -and $PreviousConfig.$section) {
            $config | Add-Member -NotePropertyName $section -NotePropertyValue $PreviousConfig.$section -Force
            Write-Host "   Kept the $section settings of the earlier install" -ForegroundColor Gray
        }
    }
    # The agent trusts only this program on its pipe (club accounts, lock screen status): the lock screen of this install
    if (-not $config.AccountPool) {
        $config | Add-Member -NotePropertyName AccountPool -NotePropertyValue ([pscustomobject]@{ Platforms = [pscustomobject]@{} }) -Force
    }
    $config.AccountPool | Add-Member -NotePropertyName LauncherPath -NotePropertyValue (Join-Path $LauncherPath "Cms.Launcher.exe") -Force
    $config | ConvertTo-Json -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}

# Stops the agent service. Its start type is "disabled" during the update, so its restart actions cannot start the old
# program again while its files are replaced. A service that does not stop within 30 s is ended.
function Stop-Agent([string]$Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) { return }
    sc.exe config $Name start= disabled | Out-Null
    if ($service.Status -ne 'Stopped') {
        try { Stop-Service -Name $Name -Force -ErrorAction Stop } catch { Write-Warning "Stop-Service ${Name}: $($_.Exception.Message)" }
    }
    for ($i = 0; $i -lt 30; $i++) {
        if ((Get-Service -Name $Name).Status -eq 'Stopped') { return }
        Start-Sleep -Seconds 1
    }
    $processId = (Get-CimInstance Win32_Service -Filter "Name='$Name'").ProcessId
    if ($processId) {
        Write-Warning "The service $Name did not stop within 30 s; ending its process $processId"
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

# Ends every process of the lock screen program in the launcher folder (the lock screen and its watchdog, in all
# sessions), so its files can be replaced. Several rounds: a watchdog may start a lock screen again for a moment.
# Returns the sessions of the kiosk user that had a lock screen: they are signed out at the end, and the automatic
# sign-in of ClubUser starts the new lock screen.
function Stop-LockScreens([string]$Folder, [string]$KioskUser) {
    $sessions = @{}
    $prefix = ($Folder -replace '/', '\').TrimEnd('\') + '\'
    for ($round = 0; $round -lt 10; $round++) {
        $found = @(Get-CimInstance Win32_Process -Filter "Name='Cms.Launcher.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
        if ($found.Count -eq 0) { return @($sessions.Keys) }
        foreach ($p in $found) {
            try {
                $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction Stop
                if ($owner.User -eq $KioskUser) { $sessions[[int]$p.SessionId] = $true }
            } catch { }
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 700
    }
    Write-Warning "A lock screen process of $Folder still runs; the copy may fail (then the old version is restored)"
    return @($sessions.Keys)
}

# Signs out the given Windows sessions (the kiosk user). With ForceAutoLogon, ClubUser signs in again at once.
function Invoke-SessionLogoff([object[]]$Sessions) {
    foreach ($id in $Sessions) {
        $ErrorActionPreference = 'Continue'
        & logoff.exe $id 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "   Signed out the session $id of ClubUser: the automatic sign-in starts the lock screen" -ForegroundColor Gray
        } else {
            Write-Warning "Could not sign out the session $id (logoff.exe exit code $LASTEXITCODE). Restart the PC."
        }
    }
}

# Waits until the service runs. False after the given seconds.
function Wait-ServiceRunning([string]$Name, [int]$Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

# The lock screens that this run stopped come back: the watchdog task is on again, and the sessions of the kiosk user
# are signed out (the automatic sign-in starts the lock screen). Runs once, at the end or on a failure.
function Resume-LockScreens {
    if ($script:watchTask) {
        $script:watchTask | Enable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null
        $script:watchTask = $null
    }
    if ($script:stoppedSessions.Count -gt 0) {
        $sessions = $script:stoppedSessions
        $script:stoppedSessions = @()
        Invoke-SessionLogoff $sessions
    }
}

# After a failed copy or start: the files of the old version back, the old agent started, and the lock screens back
# (Resume-LockScreens). The PC is never left without the agent. True when the old version runs again.
function Restore-OldVersion {
    Write-Host "   Restoring the old version..." -ForegroundColor Yellow
    $restored = $true
    if ($script:agentBackupReady) { if (-not (Copy-Folder $agentBackup $AgentPath -Mirror)) { $restored = $false } }
    if ($script:launcherBackupReady) { if (-not (Copy-Folder $launcherBackup $LauncherPath -Mirror)) { $restored = $false } }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        sc.exe config $serviceName start= auto | Out-Null
        sc.exe start $serviceName | Out-Null
        if (-not (Wait-ServiceRunning $serviceName 30)) { $restored = $false }
    }
    Resume-LockScreens
    if ($restored) { Write-Host "   The old version runs again" -ForegroundColor Yellow }
    return $restored
}

if (-not (Test-IsAdmin)) {
    Write-Error "Please run this script as Administrator."
    exit 1
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Club Management System - Client Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Project by Volodymyr Shabat" -ForegroundColor DarkGray
Write-Host ""
# Last safety net: an unexpected error ends the install with exit code 1, and a lock screen that this run stopped comes
# back (Stop-Install). Steps 3 and 4 have their own handler that restores the old version first.
trap {
    Stop-Install "Unexpected error at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentFiles = Join-Path $scriptDir "agent"
$launcherFiles = Join-Path $scriptDir "launcher"
$serviceName = "ClubAgent"
$kioskUserName = "ClubUser"
$exePath = Join-Path $AgentPath "Cms.Agent.Service.exe"
$launcherExe = Join-Path $LauncherPath "Cms.Launcher.exe"
$agentStage = "$AgentPath.new"
$launcherStage = "$LauncherPath.new"
$agentBackup = "$AgentPath.old"
$launcherBackup = "$LauncherPath.old"
$script:agentBackupReady = $false
$script:launcherBackupReady = $false
$script:watchTask = $null
$script:stoppedSessions = @()
$signedOut = 0

# Step 1: Check the package and prepare everything while the old version still runs: the new files are staged in
# C:\ClubAgent.new and C:\ClubLauncher.new (with the merged agent settings), the old files are copied to
# C:\ClubAgent.old and C:\ClubLauncher.old. A failure here changes nothing on the PC.
Write-Host "[1/8] Checking the package and preparing the files..." -ForegroundColor Yellow
if (!(Test-Path (Join-Path $agentFiles "Cms.Agent.Service.exe"))) {
    Stop-Install "Agent files not found at: $agentFiles. Extract the agent zip and the launcher zip into the same folder. Nothing was changed."
}
$haveLauncher = Test-Path (Join-Path $launcherFiles "Cms.Launcher.exe")
if (-not $haveLauncher) {
    Write-Warning "Launcher files not found at: $launcherFiles. The lock screen is not installed or updated."
}
$isUpdate = Test-Path $exePath
$previousConfig = $null
if (Test-Path (Join-Path $AgentPath "appsettings.json")) {
    try { $previousConfig = [IO.File]::ReadAllText((Join-Path $AgentPath "appsettings.json")) | ConvertFrom-Json } catch { }
}
try {
    foreach ($path in @($AgentPath, $LauncherPath)) {
        if (!(Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    }
    New-ProtectedFolder $agentStage
    if (-not (Copy-Folder $agentFiles $agentStage)) { throw "Could not copy $agentFiles to $agentStage" }
    Set-AgentConfig -Path (Join-Path $agentStage "appsettings.json") -PreviousConfig $previousConfig
    if ($haveLauncher) {
        New-ProtectedFolder $launcherStage
        if (-not (Copy-Folder $launcherFiles $launcherStage)) { throw "Could not copy $launcherFiles to $launcherStage" }
    }
    if ($isUpdate) {
        New-ProtectedFolder $agentBackup
        if (-not (Copy-Folder $AgentPath $agentBackup)) { throw "Could not back up $AgentPath to $agentBackup" }
        $script:agentBackupReady = $true
    }
    if (Test-Path $launcherExe) {
        New-ProtectedFolder $launcherBackup
        if (-not (Copy-Folder $LauncherPath $launcherBackup)) { throw "Could not back up $LauncherPath to $launcherBackup" }
        $script:launcherBackupReady = $true
    }
} catch {
    Stop-Install "The install did not start: $($_.Exception.Message). Nothing was changed; the PC works as before."
}
if ($isUpdate) { Write-Host "   Update: the old version is backed up in $agentBackup and $launcherBackup" -ForegroundColor Gray }

# Step 2: The data folder of the agent (permissions, event source, server URL for the lock screen, enrollment token)
Write-Host "[2/8] Configuring the agent data folder..." -ForegroundColor Yellow
$programDataConfigDir = Join-Path $env:ProgramData "ClubAgent"
$privateDir = Join-Path $programDataConfigDir "private"
foreach ($dir in @($programDataConfigDir, $privateDir)) {
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

# Permissions (SIDs, so this works on every Windows language):
#   ClubAgent         SYSTEM, Administrators: full; agent (LocalService): modify; Users: read only.
#                     Players can read the lock state and the PIN hash, but cannot create or change
#                     files there (a planted state.json or staff-pin.json would unlock the PC).
#   ClubAgent\private SYSTEM, Administrators: full; agent: modify; no access for players (device key, token).
# /setowner: files left by older versions are owned by Administrators afterwards.
icacls $programDataConfigDir /setowner "*S-1-5-32-544" /T /C | Out-Null
icacls $programDataConfigDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)M" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
icacls $privateDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set the permissions of $programDataConfigDir" }
#   ClubAgent\inbox   the lock screen drops a note of each emergency PIN unlock here; the agent sends it
#                     to the server audit log. Users: create files only (WD = add file, this folder only);
#                     OWNER RIGHTS (S-1-3-4): read permissions only, so a player cannot read, change or
#                     delete the note afterwards.
$inboxDir = Join-Path $programDataConfigDir "inbox"
if (!(Test-Path $inboxDir)) { New-Item -ItemType Directory -Path $inboxDir -Force | Out-Null }
icacls $inboxDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)M" "*S-1-5-32-545:(WD,S)" "*S-1-3-4:(OI)(IO)(RC)" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set the permissions of $inboxDir" }
#   ClubAgent\logs    the text log of the agent (agent.log, at most 4 files of 1 MB): SYSTEM, Administrators: full;
#                     agent: modify; no access for players.
$logsDir = Join-Path $programDataConfigDir "logs"
if (!(Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -Force | Out-Null }
icacls $logsDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set the permissions of $logsDir" }

# The event source of the agent in the Application log. LocalService cannot create it; without it Windows drops the
# warnings and errors of the agent ("The source Cms.Agent.Service was not found").
try {
    if (-not [System.Diagnostics.EventLog]::SourceExists('Cms.Agent.Service')) {
        [System.Diagnostics.EventLog]::CreateEventSource('Cms.Agent.Service', 'Application')
        Write-Host "   Event source Cms.Agent.Service registered (Application log)" -ForegroundColor Gray
    }
} catch {
    Write-Warning "Could not register the event source Cms.Agent.Service: $($_.Exception.Message). The agent still writes $logsDir\agent.log"
}

# Earlier versions kept the device key in the readable folder; the agent moves it, remove a stale copy
$legacyDeviceFile = Join-Path $programDataConfigDir "device.json"
if ((Test-Path $legacyDeviceFile) -and !(Test-Path (Join-Path $privateDir "device.json"))) {
    Move-Item $legacyDeviceFile (Join-Path $privateDir "device.json") -Force
}

@{
    serverUrl = $ServerUrl
} | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $programDataConfigDir "config.json")

# The enrollment token is needed only for the first registration of the PC. A PC with a device registration
# (private\device.json, for example an update) gets no token file, and a token file of an earlier run is removed:
# the token must not stay on the PCs. If the server does not know the PC any more, approve it in the admin UI.
$deviceFile = Join-Path $privateDir "device.json"
$tokenFile = Join-Path $privateDir "enrollment-token.txt"
if (Test-Path $deviceFile) {
    if (Test-Path $tokenFile) {
        Remove-Item -Path $tokenFile -Force -ErrorAction SilentlyContinue
        Write-Host "   Removed the enrollment token file of an earlier install" -ForegroundColor Gray
    }
    if ($EnrollmentToken) {
        Write-Host "   This PC is registered already: the enrollment token is not stored (not needed)" -ForegroundColor Gray
    }
} elseif ($EnrollmentToken) {
    Set-Content -Path $tokenFile -Value $EnrollmentToken.Trim() -NoNewline
    if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
        Write-Warning "The enrollment token is used only with -ServerUrl. With auto-discovery the PC waits for approval in the admin UI."
    } else {
        Write-Host "   Enrollment token stored (the agent deletes it after the registration)" -ForegroundColor Gray
    }
} else {
    Write-Host "   No enrollment token: approve this PC in the admin UI (Devices) after the first start" -ForegroundColor Gray
}

if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    Write-Host "   Server URL: Auto-discovery (LAN broadcast)" -ForegroundColor Gray
} else {
    Write-Host "   Server URL: $ServerUrl" -ForegroundColor Gray
}

# Steps 3 and 4 run with the agent stopped: any error there restores the old version and starts it again.
$copyError = $null
try {
    # Step 3: Stop the agent and the lock screen (its watchdog too: the scheduled task is off during the copy)
    Write-Host "[3/8] Stopping the agent and the lock screen..." -ForegroundColor Yellow
    $script:watchTask = Get-ScheduledTask -TaskName 'ClubLauncherWatchdog' -ErrorAction SilentlyContinue
    if ($script:watchTask) { $script:watchTask | Disable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null }
    Stop-Agent $serviceName
    $script:stoppedSessions = @(Stop-LockScreens -Folder $LauncherPath -KioskUser $kioskUserName)
    $signedOut = $script:stoppedSessions.Count
    if ($signedOut -gt 0) {
        Write-Host "   Lock screen stopped in the session(s) $($script:stoppedSessions -join ', ') of $kioskUserName (signed out at the end)" -ForegroundColor Gray
    }

    # Step 4: Copy the new version
    Write-Host "[4/8] Copying the new version..." -ForegroundColor Yellow
    if (-not (Copy-Folder $agentStage $AgentPath)) {
        $copyError = "Could not copy the agent files to $AgentPath"
    } elseif ($haveLauncher -and -not (Copy-Folder $launcherStage $LauncherPath)) {
        $copyError = "Could not copy the lock screen files to $LauncherPath (a file is in use?)"
    }
} catch {
    $copyError = "Unexpected error: $($_.Exception.Message)"
}
if ($copyError) {
    $restored = Restore-OldVersion
    if ($restored -and $isUpdate) { Stop-Install "UPDATE FAILED: $copyError. The old version is restored and runs again; nothing else was changed." }
    Stop-Install "INSTALL FAILED: $copyError. Restore the old files from $agentBackup and $launcherBackup by hand, or run the installer again."
}

# Program folders: C:\ClubAgent and C:\ClubLauncher inherit "Authenticated Users: Modify" from C:\, so a
# player could replace the service program or its settings. Only SYSTEM and Administrators may change them;
# the agent (LocalService) and the players may read and run them.
foreach ($path in @($AgentPath, $LauncherPath)) {
    icacls $path /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-19:(OI)(CI)RX" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not restrict the permissions of $path" }
}

# Step 5: The agent service (LocalService), its restart actions, and its start. An existing service with the right
# account is changed in place (a deleted service can stay "marked for deletion" and block a new one).
Write-Host "[5/8] Installing and starting the agent service..." -ForegroundColor Yellow
$binPath = "`"$exePath`""
$existing = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
if ($existing -and $existing.StartName -eq 'NT AUTHORITY\LocalService') {
    sc.exe config $serviceName binPath= $binPath start= auto type= own | Out-Null
} else {
    if ($existing) {
        sc.exe delete $serviceName | Out-Null   # another account (for example a test as LocalSystem): back to LocalService
        Start-Sleep -Seconds 2
    }
    sc.exe create $serviceName binPath= $binPath start= auto obj= "NT AUTHORITY\LocalService" type= own | Out-Null
}
if ($LASTEXITCODE -ne 0) { Write-Warning "sc.exe could not install the service $serviceName (exit code $LASTEXITCODE)" }
sc.exe description $serviceName "Club Agent Service - Gaming PC management agent" | Out-Null
Set-ServiceRecovery $serviceName
sc.exe start $serviceName | Out-Null
if (-not (Wait-ServiceRunning $serviceName 30)) {
    if ($isUpdate) {
        $restored = Restore-OldVersion
        if ($restored) { Stop-Install "UPDATE FAILED: the new agent did not start within 30 s. The old version is restored and runs again." }
        Stop-Install "UPDATE FAILED: the new agent did not start, and the old version could not be restored. Check $programDataConfigDir\logs\agent.log."
    }
    Stop-Install "The agent service did not start within 30 s. Check the Application event log and $programDataConfigDir\logs\agent.log."
}
$agentVersion = (Get-Item $exePath).VersionInfo.FileVersion
Write-Host "   Agent service running (version $agentVersion)" -ForegroundColor Gray

# Step 6: Configure Launcher autostart
Write-Host "[6/8] Configuring Launcher autostart..." -ForegroundColor Yellow

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
$adminUser = "ClubAdmin"
$adminReady = $false
if ($KioskMode) {
    Write-Host "[7/8] Setting up the kiosk account..." -ForegroundColor Yellow

    $kioskUser = "ClubUser"
    $winlogonPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    if (-not ('ClubInstall.KioskNative' -as [type])) {
        Add-Type -TypeDefinition $kioskNativeSource -IgnoreWarnings
    }

    # 7a: Recovery account ClubAdmin (Administrators). Groups by SID, so this works on every Windows language.
    if (Get-LocalUser -Name $adminUser -ErrorAction SilentlyContinue) {
        Write-Host "   Admin '$adminUser' already exists" -ForegroundColor Gray
        $adminReady = $true
    } else {
        $adminPass = $AdminPassword
        if ($null -eq $adminPass -and (Test-InteractiveSession)) {
            Write-Host "   Creating '$adminUser' recovery account..." -ForegroundColor Cyan
            $adminPass = Read-Host "   Enter password for $adminUser (staff recovery account)" -AsSecureString
        }
        if ($null -eq $adminPass -or $adminPass.Length -eq 0) {
            Write-Warning ("No '$adminUser' account was created: no -AdminPassword and no interactive console. " +
                "Run the installer again with -AdminPassword, or create a local administrator for the staff.")
        } else {
            try {
                New-LocalUser -Name $adminUser `
                    -Password $adminPass `
                    -FullName "Club Administrator" `
                    -Description "Staff recovery account for kiosk PCs" `
                    -PasswordNeverExpires | Out-Null
                Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $adminUser
            } catch {
                Stop-Install "Could not create the account '$adminUser': $($_.Exception.Message)"
            }
            $adminReady = $true
            Write-Host "   Admin '$adminUser' created (Administrators group)" -ForegroundColor Green
        }
    }

    # 7b: The restricted account ClubUser with a new random password (a re-run rotates it)
    Write-Host "   Creating '$kioskUser' account..." -ForegroundColor Gray
    $kioskPassword = New-KioskPassword
    try {
        if (Get-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue) {
            Write-Host "   User '$kioskUser' already exists, setting a new password..." -ForegroundColor Gray
            Set-LocalUser -Name $kioskUser -Password $kioskPassword -PasswordNeverExpires $true
        } else {
            New-LocalUser -Name $kioskUser `
                -Password $kioskPassword `
                -FullName "Club Gaming Station" `
                -Description "Restricted kiosk account for PC Club" `
                -PasswordNeverExpires `
                -UserMayNotChangePassword | Out-Null
        }
        $kioskSid = (Get-LocalUser -Name $kioskUser).SID.Value
    } catch {
        Stop-Install "Could not create the account '$kioskUser': $($_.Exception.Message)"
    }
    # Users (may sign in), never Administrators
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $kioskUser -ErrorAction SilentlyContinue
    Remove-LocalGroupMember -SID 'S-1-5-32-544' -Member $kioskUser -ErrorAction SilentlyContinue
    Write-Host "   Account '$kioskUser' ready (restricted, non-admin, random password)" -ForegroundColor Gray

    # 7c: The profile of ClubUser (C:\Users\ClubUser\NTUSER.DAT), also when the installer runs as SYSTEM
    $profilePath = New-KioskProfile -UserName $kioskUser -Sid $kioskSid -Password $kioskPassword
    if (-not $profilePath) {
        Stop-Install ("Windows has no profile with NTUSER.DAT for '$kioskUser'. Delete the profile of '$kioskUser' " +
            "(System Properties > Advanced > User Profiles), then run the installer again.")
    }

    # 7d: The lock screen as the shell of ClubUser, Task Manager and the Start context menus off: in the hive of
    # ClubUser only. Never machine-wide: that would also give ClubAdmin the lock screen as its shell.
    Write-Host "   Setting Launcher as the shell of '$kioskUser'..." -ForegroundColor Gray
    $hive = Mount-KioskHive -Sid $kioskSid -ProfilePath $profilePath
    if ($null -eq $hive) {
        Stop-Install ("Could not load the registry hive of '$kioskUser' ($profilePath\NTUSER.DAT). Sign out '$kioskUser' " +
            "or restart the PC, then run the installer again. The machine-wide shell was not changed.")
    }
    $policyError = $null
    try {
        Set-KioskUserPolicies -Root $hive.Root -LauncherExe $launcherExe
    } catch {
        $policyError = $_.Exception.Message
    } finally {
        Dismount-KioskHive $hive
    }
    if ($policyError) { Stop-Install "Could not write the shell and the policies of '$kioskUser': $policyError" }
    Write-Host "   Shell set to the lock screen watchdog ('$kioskUser' only)" -ForegroundColor Gray
    Register-LockScreenTask -UserName $kioskUser -LauncherExe $launcherExe
    Write-Host "   Task Manager and the Start context menus disabled ('$kioskUser' only)" -ForegroundColor Gray

    # 7e: Undo the machine-wide settings of older installers (they also locked out ClubAdmin)
    $machineShell = (Get-ItemProperty -Path $winlogonPath -Name 'Shell' -ErrorAction SilentlyContinue).Shell
    if ($machineShell -and $machineShell -match 'Cms\.Launcher') {
        Set-ItemProperty -Path $winlogonPath -Name 'Shell' -Value 'explorer.exe'
        Write-Host "   Machine-wide shell of an older install restored to explorer.exe" -ForegroundColor Gray
    }
    Remove-MachineValue -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name 'DisableTaskMgr' -What 'DisableTaskMgr'
    Remove-MachineValue -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer" -Name 'DisableContextMenusInStart' -What 'DisableContextMenusInStart'

    # Step 8: Automatic sign-in of ClubUser. The password goes to the LSA secret "DefaultPassword" (only SYSTEM
    # can read it), never to the registry value DefaultPassword (local users can read the Winlogon key).
    Write-Host "[8/8] Configuring auto-login..." -ForegroundColor Yellow
    try {
        [ClubInstall.KioskNative]::SetAutoLogonPassword($kioskPassword)
    } catch {
        Stop-Install ("Could not store the auto-login password of '$kioskUser' as an LSA secret: $($_.Exception.Message). " +
            "Run the installer again.")
    } finally {
        $kioskPassword.Dispose()
    }
    Remove-ItemProperty -Path $winlogonPath -Name "DefaultPassword" -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $winlogonPath -Name "DefaultUserName" -Value $kioskUser
    Set-ItemProperty -Path $winlogonPath -Name "DefaultDomainName" -Value $env:COMPUTERNAME
    Set-ItemProperty -Path $winlogonPath -Name "AutoAdminLogon" -Value "1"
    Set-ItemProperty -Path $winlogonPath -Name "ForceAutoLogon" -Value "1"
    Write-Host "   Auto-login enabled for '$kioskUser' (password in LSA, not in the registry)" -ForegroundColor Gray

    # Keyboard hooks (Alt+Tab, Alt+F4, Windows key) are managed by the Launcher.
    # Write kiosk config marker for Launcher to detect.
    # No PIN here: the staff PIN is set by the owner in the admin UI (Settings); the agent receives
    # only its hash from the server and the launcher verifies it locally (also offline).
    $kioskConfig = Join-Path $LauncherPath "kiosk.json"
    @{
        KioskMode = $true
        KioskUser = $kioskUser
        BlockAltTab = $true
        BlockAltF4 = $true
        BlockWinKey = $true
        InstalledUtc = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json | Set-Content -Path $kioskConfig
    Write-Host "   Kiosk config written to $kioskConfig" -ForegroundColor Gray

} else {
    Write-Host "[7/8] Kiosk mode: Skipped (use -KioskMode to enable)" -ForegroundColor Gray
    Write-Host "[8/8] Auto-login: Skipped" -ForegroundColor Gray
}

# The lock screens that this run stopped come back with the new version (the sessions of ClubUser are signed out; the
# automatic sign-in starts the new lock screen), and the staged files and the backup are removed.
Resume-LockScreens
foreach ($dir in @($agentStage, $launcherStage, $agentBackup, $launcherBackup)) {
    if (Test-Path $dir) { Remove-Item -Path $dir -Recurse -Force -ErrorAction SilentlyContinue }
}
$launcherVersion = $null
if (Test-Path $launcherExe) { $launcherVersion = (Get-Item $launcherExe).VersionInfo.FileVersion }

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
if ($isUpdate) {
    Write-Host "  RESULT: OK - the club PC software is updated" -ForegroundColor Green
} else {
    Write-Host "  RESULT: OK - Client Installation Complete" -ForegroundColor Green
}
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Agent service: running (ClubAgent, version $agentVersion; log $programDataConfigDir\logs\agent.log)" -ForegroundColor Cyan
if ($launcherVersion) {
    Write-Host "Lock screen: $launcherExe (version $launcherVersion)" -ForegroundColor Cyan
} else {
    Write-Host "Lock screen: not installed (no launcher files in the package)" -ForegroundColor Yellow
}
if ($signedOut -gt 0) {
    Write-Host "$kioskUserName was signed out; the automatic sign-in starts the new lock screen." -ForegroundColor Cyan
}
Write-Host "The PC stays locked until a session starts or staff unlock it in the admin UI." -ForegroundColor Cyan

if ($KioskMode) {
    Write-Host ""
    Write-Host "KIOSK MODE ENABLED:" -ForegroundColor Magenta
    Write-Host "  - User: ClubUser (auto-login on boot; random password, new on every install run)" -ForegroundColor White
    Write-Host "  - Shell: Cms.Launcher.exe --watchdog (replaces explorer for ClubUser only; restarts the lock screen)" -ForegroundColor White
    Write-Host "  - Staff unlock: Ctrl+Shift+U + the staff PIN (set it in the admin UI, Settings)" -ForegroundColor White
    Write-Host "  - The lock screen stays locked when the agent stops (fail closed)" -ForegroundColor White
    if ($adminReady) {
        Write-Host "  - Switch to admin: Ctrl+Alt+Del > Switch User > $adminUser" -ForegroundColor White
    } else {
        Write-Host "  - No $adminUser account: create a local administrator before you need to leave the kiosk" -ForegroundColor Yellow
    }
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
if ($isUpdate) {
    Write-Host "No restart is needed for this update." -ForegroundColor Yellow
} else {
    Write-Host "Restart the PC for all changes to take effect." -ForegroundColor Yellow
}
Write-Host ""
