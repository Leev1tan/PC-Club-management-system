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

# ---- BEGIN safe folders (the same text in install-client.ps1, install-server.ps1 and uninstall-kiosk.ps1) ----
# Security review of 3aa3619: every user may create folders in C:\ and C:\ProgramData, and the player owns the profile of
# ClubUser. A junction or a symbolic link there in place of a folder of the installer made the installer delete or change
# ANOTHER folder as SYSTEM: Windows PowerShell 5.1 follows a junction in Remove-Item -Recurse, and icacls changes the
# target of a link. So the installers never delete, copy into or change the permissions of a folder that is a reparse
# point (junction, symbolic link, mount point) or that belongs to somebody other than SYSTEM or an administrator, and a
# delete never enters a reparse point inside a folder: the link itself is removed, its target stays.

# The reason why an existing folder must not be used, or $null. Only the attributes and the owner SID: tested by the
# installer self-check.
function Get-UnsafeFolderReason([IO.FileAttributes]$Attributes, [string]$OwnerSid, [string[]]$TrustedOwners) {
    if (($Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return 'it is a junction or a symbolic link' }
    if ([string]::IsNullOrEmpty($OwnerSid)) { return 'its owner cannot be read' }
    if ($TrustedOwners -notcontains $OwnerSid) { return "its owner ($OwnerSid) is not SYSTEM or an administrator" }
    return $null
}

# SYSTEM, Administrators, TrustedInstaller, the account of this run, the members of the local Administrators group, and
# the extra SIDs (for example LocalService for the data folder of the agent).
function Get-TrustedOwners([string[]]$Extra = @()) {
    $list = New-Object 'System.Collections.Generic.List[string]'
    foreach ($sid in (@('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') + $Extra)) { $list.Add($sid) }
    $self = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($self) { $list.Add($self.Value) }
    try {
        foreach ($member in @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop)) { if ($member.SID) { $list.Add($member.SID.Value) } }
    } catch {
        # Get-LocalGroupMember fails on a group with an orphaned member (a deleted domain account): read it with ADSI
        try {
            $name = (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
            foreach ($member in @(([ADSI]"WinNT://./$name,group").psbase.Invoke('Members'))) {
                $bytes = $member.GetType().InvokeMember('objectSid', 'GetProperty', $null, $member, $null)
                if ($bytes) { $list.Add((New-Object Security.Principal.SecurityIdentifier($bytes, 0)).Value) }
            }
        } catch {
            # then only the accounts above count
        }
    }
    return ,$list.ToArray()
}

# The reason why this path must not be used, or $null (it may be used, or it does not exist).
function Get-FolderProblem([string]$Path, [string[]]$TrustedOwners) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (-not $item) { return $null }
    $owner = $null
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        try { $owner = (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { $owner = $null }
    }
    return Get-UnsafeFolderReason $item.Attributes $owner $TrustedOwners
}

# The first reparse point inside a folder (the walk never enters one), or $null.
function Find-ReparsePoint([string]$Path) {
    foreach ($entry in [IO.Directory]::GetFileSystemEntries($Path)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $entry }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            $inner = Find-ReparsePoint $entry
            if ($inner) { return $inner }
        }
    }
    return $null
}

# What to do with a link that must not be used: remove the link only. "rmdir" removes a folder link and "del" a file
# link; neither touches the target of the link.
function Get-LinkAdvice([string]$Path) {
    return ("$Path is a junction or a symbolic link. A player may have made it. Remove the link only with " +
        "cmd /c rmdir `"$Path`" (a link to a file: cmd /c del `"$Path`"): the folder or file that it points to stays.")
}

# The message for a folder that must not be used (Get-FolderProblem): what is wrong and what to do. Advice to change the
# owner applies only to a real folder: it cannot help for a link.
function Get-FolderAdvice([string]$Path, [string]$Problem) {
    if ($Problem -match 'junction') { return Get-LinkAdvice $Path }
    return ("$Path cannot be used: $Problem. A player may have made it: check its content and remove it by hand. If an " +
        "administrator made it, make Administrators its owner with icacls `"$Path`" /setowner *S-1-5-32-544 and run the installer again.")
}

# The attributes that a delete needs, or $null when nothing must change. Windows refuses to delete a read-only file or a
# read-only folder ("Access to the path ... is denied"), and every user profile has read-only folders (Desktop, Documents,
# Downloads, AppData\Local\Microsoft\Windows\WinX\Group1 ...): only ReadOnly is removed. Hidden and System do not block a
# delete, and Windows refuses to change the attributes of some files of a profile: SFAP\cache0.bin (Hidden, System) of
# AppData\Local\Microsoft\Windows may be deleted by Administrators, not changed (Windows re-test 2026-11-28 night,
# defect 2). A reparse point is never changed: the change could apply to the target of the link.
function Get-DeletableAttributes([IO.FileAttributes]$Attributes) {
    if (($Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $null }
    $readOnly = [int][IO.FileAttributes]::ReadOnly
    if (([int]$Attributes -band $readOnly) -eq 0) { return $null }
    $rest = [int]$Attributes -band (-bnot $readOnly)
    if ($rest -eq 0) { return [IO.FileAttributes]::Normal }
    return [IO.FileAttributes]$rest
}

# Changes the attributes of one folder or file (one call, so that the installer self-check can model a refusal).
function Set-ItemAttributes([string]$Path, [IO.FileAttributes]$Attributes) {
    [IO.File]::SetAttributes($Path, $Attributes)
}

# The short reason of a failed .NET call ("Access to the path ... is denied."), without "Exception calling ...".
function Get-ErrorReason($ErrorRecord) {
    $e = $ErrorRecord.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    return $e.Message
}

# Deletes a folder and its content without entering a reparse point: a junction or a link inside is removed as a link.
# The ReadOnly attribute of the folders and files is removed first (Get-DeletableAttributes), never that of a link. When
# Windows refuses that change, the delete is tried anyway. An item that cannot be deleted does not stop the walk: it is
# added to $Failed ("<path>: <reason>"), and only the folders that contain it stay. Without $Failed the function throws at
# the end when an item stays.
function Remove-TreeNoFollow([string]$Path, $Failed = $null) {
    $throwAtEnd = $null -eq $Failed
    if ($throwAtEnd) { $Failed = New-Object 'System.Collections.Generic.List[string]' }
    $before = $Failed.Count
    try {
        $own = Get-DeletableAttributes ([IO.File]::GetAttributes($Path))
        if ($null -ne $own) { try { Set-ItemAttributes $Path $own } catch { } }
        foreach ($entry in [IO.Directory]::GetFileSystemEntries($Path)) {
            try {
                $attributes = [IO.File]::GetAttributes($entry)
                $isFolder = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    if ($isFolder) { [IO.Directory]::Delete($entry, $false) } else { [IO.File]::Delete($entry) }
                } elseif ($isFolder) {
                    Remove-TreeNoFollow $entry $Failed
                } else {
                    $deletable = Get-DeletableAttributes $attributes
                    if ($null -ne $deletable) { try { Set-ItemAttributes $entry $deletable } catch { } }
                    [IO.File]::Delete($entry)
                }
            } catch {
                $Failed.Add("${entry}: $(Get-ErrorReason $_)")
            }
        }
        if ($Failed.Count -eq $before) { [IO.Directory]::Delete($Path, $false) }
    } catch {
        $Failed.Add("${Path}: $(Get-ErrorReason $_)")
    }
    if ($throwAtEnd -and $Failed.Count -gt 0) { throw "$($Failed.Count) item(s) could not be deleted, the first: $($Failed[0])" }
}

# The first account that may change the content of a folder (write, add, delete, change the permissions or the owner)
# and is not trusted, or $null. Rules: the access rules of the folder; inherit-only rules do not apply to the folder.
function Get-ForeignWriter($Rules, [string[]]$TrustedOwners) {
    # WriteData, AppendData, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles, WriteAttributes, Delete,
    # ChangePermissions, TakeOwnership, GENERIC_ALL, GENERIC_WRITE
    $write = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    foreach ($rule in @($Rules)) {
        if ("$($rule.AccessControlType)" -ne 'Allow') { continue }
        if (("$($rule.PropagationFlags)" -split ',\s*') -contains 'InheritOnly') { continue }
        if (([long]$rule.FileSystemRights -band $write) -eq 0) { continue }
        $sid = "$($rule.IdentityReference)"
        if ($TrustedOwners -notcontains $sid) { return $sid }
    }
    return $null
}

# Removes a folder (a staged or backed-up version, a leftover of an earlier version, a profile). Refused with a warning
# when Get-FolderProblem finds a problem, or when an untrusted account may still change the folder (it could swap a
# folder inside for a junction during the delete). True when the folder is gone.
function Remove-FolderSafely([string]$Path, [string[]]$TrustedOwners) {
    $problem = Get-FolderProblem $Path $TrustedOwners
    if ($problem) {
        Write-Warning "Not removed: $Path ($problem). Check it and remove it by hand."
        return $false
    }
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    try {
        $writer = Get-ForeignWriter ((Get-Acl -LiteralPath $Path).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) $TrustedOwners
    } catch {
        $writer = 'unknown (the permissions cannot be read)'
    }
    if ($writer) {
        Write-Warning "Not removed: $Path (the account $writer may change it). Check it and remove it by hand."
        return $false
    }
    # Every item that can be deleted is deleted; the items that stay are listed once at the end
    $failed = New-Object 'System.Collections.Generic.List[string]'
    try {
        Remove-TreeNoFollow $Path $failed
    } catch {
        $failed.Add("${Path}: $(Get-ErrorReason $_)")
    }
    if ($failed.Count -eq 0) { return $true }
    Write-Warning "Could not remove ${Path}: $($failed.Count) item(s) stay:"
    foreach ($item in @($failed | Select-Object -First 10)) { Write-Warning "  $item" }
    if ($failed.Count -gt 10) { Write-Warning "  ... and $($failed.Count - 10) more" }
    return $false
}

# A protected DACL (no inherited entries) from icacls-style grants "*<SID>:(OI)(CI)F", "M" or "RX".
function New-FolderSecurity([string[]]$Grants) {
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $rights = @{ 'F' = 'FullControl'; 'M' = 'Modify'; 'RX' = 'ReadAndExecute' }
    foreach ($grant in $Grants) {
        if ($grant -notmatch '^\*(S-[0-9-]+):\(OI\)\(CI\)(F|M|RX)$') { throw "Unknown permission: $grant" }
        $sid = New-Object System.Security.Principal.SecurityIdentifier($Matches[1])
        $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, $rights[$Matches[2]], 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    return $security
}

# A folder of the installer. A missing one is created WITH its permissions in one call: a new folder in C:\ would inherit
# "Authenticated Users: Modify" for a moment, and a player could replace it with a junction or put a program into it. An
# existing one must be a plain folder of SYSTEM or an administrator (Get-FolderProblem). Throws otherwise.
function Initialize-ProtectedFolder([string]$Path, [string[]]$Grants, [string[]]$TrustedOwners) {
    $problem = Get-FolderProblem $Path $TrustedOwners
    if ($problem) { throw (Get-FolderAdvice $Path $problem) }
    if (-not (Test-Path -LiteralPath $Path)) {
        $security = New-FolderSecurity $Grants
        if ([IO.Directory].GetMethod('CreateDirectory', [Type[]]@([string], [System.Security.AccessControl.DirectorySecurity]))) {
            [IO.Directory]::CreateDirectory($Path, $security) | Out-Null                        # Windows PowerShell 5.1
        } else {
            [IO.FileSystemAclExtensions]::Create((New-Object IO.DirectoryInfo($Path)), $security) # PowerShell 7
        }
    }
    $problem = Get-FolderProblem $Path $TrustedOwners
    if ($problem) { throw (Get-FolderAdvice $Path $problem) }
}
# ---- END safe folders ----

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

# The Windows sessions of a local account and their sign-out (wtsapi32). Windows re-test 2026-11-27 late, defect 4: the
# text of "query user" has an empty SESSIONNAME column for a disconnected session (after "Switch user"), so the old parse
# read "Disc" as the session id and ClubUser stayed signed in. The API gives the session id and the user of every session
# (active or disconnected), the same on every Windows language. C# 5 only.
$sessionSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClubUninstall
{
    public static class KioskSessions
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WtsSessionInfo
        {
            public int SessionId;
            public IntPtr WinStationName;
            public int State;
        }

        [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateSessionsW")]
        private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

        [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSQuerySessionInformationW")]
        private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);

        private const int WtsUserName = 5;
        private const int WtsDomainName = 7;

        // True when the user of a session is the local account (the domain of a local account is the computer name)
        public static bool IsUser(string sessionUser, string sessionDomain, string user, string computer)
        {
            if (string.IsNullOrEmpty(sessionUser) || !string.Equals(sessionUser, user, StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrEmpty(sessionDomain) || string.Equals(sessionDomain, computer, StringComparison.OrdinalIgnoreCase);
        }

        // The ids of the sessions (active or disconnected) of the local account on this PC
        public static int[] Of(string user, string computer)
        {
            IntPtr list;
            int count;
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out list, out count)) throw new System.ComponentModel.Win32Exception();
            List<int> ids = new List<int>();
            try
            {
                int size = Marshal.SizeOf(typeof(WtsSessionInfo));
                for (int i = 0; i < count; i++)
                {
                    WtsSessionInfo info = (WtsSessionInfo)Marshal.PtrToStructure(new IntPtr(list.ToInt64() + (long)i * size), typeof(WtsSessionInfo));
                    if (IsUser(Query(info.SessionId, WtsUserName), Query(info.SessionId, WtsDomainName), user, computer)) ids.Add(info.SessionId);
                }
            }
            finally
            {
                WTSFreeMemory(list);
            }
            return ids.ToArray();
        }

        private static string Query(int sessionId, int infoClass)
        {
            IntPtr buffer;
            int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buffer, out bytes)) return null;
            try { return Marshal.PtrToStringUni(buffer); }
            finally { WTSFreeMemory(buffer); }
        }

        // Signs the session out (programs close without a question) and waits until Windows has ended it
        public static bool LogOff(int sessionId)
        {
            return WTSLogoffSession(IntPtr.Zero, sessionId, true);
        }
    }
}
'@

# Signs the kiosk user out of all its sessions and waits until the sessions and the registry hive of its profile are gone,
# so no file of the profile is in use when the profile is deleted. True when that happened within $Seconds. The session
# list, the sign-out and the hive check are script blocks: the installer self-check runs this function with stand-ins.
function Stop-KioskSessions([string]$UserName, [string]$Sid, [int]$Seconds, [scriptblock]$ListSessions, [scriptblock]$LogOff, [scriptblock]$HiveLoaded) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $asked = @{}
    while ($true) {
        $sessions = @(& $ListSessions)
        foreach ($id in $sessions) {
            if ($asked.ContainsKey("$id")) { continue }
            $asked["$id"] = $true
            if (& $LogOff $id) {
                Write-Host "   Signed out the session $id of $UserName" -ForegroundColor Gray
            } else {
                Write-Warning "Windows refused to sign out the session $id of $UserName"
            }
        }
        $hive = [bool](& $HiveLoaded)
        if ($sessions.Count -eq 0 -and -not $hive) { return $true }
        if ((Get-Date) -ge $deadline) {
            if ($sessions.Count -gt 0) {
                Write-Warning "$UserName is still signed in (session $($sessions -join ', ')) after $Seconds s."
            } else {
                Write-Warning "Windows still has the registry hive of $UserName (HKEY_USERS\$Sid) loaded after $Seconds s: files of the profile are in use."
            }
            return $false
        }
        Start-Sleep -Seconds 1
    }
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
$script:accountNotDeleted = $false

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

# Step 2: Disable auto-login. First the task of install-client.ps1 that puts the automatic sign-in of ClubUser back at
# every start and sign-in (else it switches the automatic sign-in on again at the next sign-in).
Write-Host "[2/5] Disabling auto-login..." -ForegroundColor Yellow
if (Get-ScheduledTask -TaskName 'ClubKioskAutoLogon' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName 'ClubKioskAutoLogon' -Confirm:$false
    Write-Host "   Removed the scheduled task ClubKioskAutoLogon" -ForegroundColor Gray
}
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
        # Sign ClubUser out first (also a disconnected session) and wait until Windows has released its profile. The
        # automatic sign-in is off since step 2, so ClubUser does not sign in again.
        $signedOut = $false
        try {
            if (-not ('ClubUninstall.KioskSessions' -as [type])) { Add-Type -TypeDefinition $sessionSource -IgnoreWarnings }
            $signedOut = Stop-KioskSessions -UserName $kioskUser -Sid $sid -Seconds 60 `
                -ListSessions { [ClubUninstall.KioskSessions]::Of($kioskUser, $env:COMPUTERNAME) } `
                -LogOff { param($id) [ClubUninstall.KioskSessions]::LogOff($id) } `
                -HiveLoaded { Test-Path "Registry::HKEY_USERS\$sid" }
        } catch {
            Write-Warning "Could not read or end the Windows sessions of ${kioskUser}: $($_.Exception.Message)"
        }
        if (-not $signedOut) {
            # Never delete a profile that is in use: Windows keeps the open files, and the account would be gone
            Write-Host "   ERROR: '$kioskUser' and its profile were NOT deleted: $kioskUser is still signed in, or Windows still uses its profile." -ForegroundColor Red
            Write-Host "   Restart the PC (the automatic sign-in is off now, so $kioskUser does not sign in), then run this script again." -ForegroundColor Red
            $script:accountNotDeleted = $true
        } else {
            Remove-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue

            # Remove the profile folder. The player owned it and may have put junctions into it: they are removed as links,
            # their targets stay (Remove-Item -Recurse of Windows PowerShell 5.1 deleted the targets as administrator)
            if (Test-Path -LiteralPath $profilePath) {
                if (-not (Remove-FolderSafely $profilePath (Get-TrustedOwners @($sid)))) {
                    Write-Warning "The profile folder $profilePath was not removed completely; remove the rest by hand"
                }
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
        }
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
if ($script:accountNotDeleted) {
    Write-Host "RESULT: the account '$kioskUser' and its profile are NOT deleted (see the error in step 4)." -ForegroundColor Red
    Write-Host "Restart the PC, then run this script again and answer y." -ForegroundColor Red
    exit 1
}
