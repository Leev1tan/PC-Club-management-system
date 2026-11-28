# Server Installer Script for Windows
# Run as Administrator
# Usage: .\install-server.ps1 [-PostgresConnectionString "Host=...;Database=cms;Username=...;Password=..."]
#
# Data directory (-DataPath, default C:\ProgramData\PCClub): SQLite database cms.db, login cookie
# keys, one-time setup code. Only SYSTEM and Administrators can open it.
# First start: the installer shows the one-time setup code; open the admin UI and create the Owner.
# -EnrollmentToken: secret for the club PC installer. Empty = keep the token of an earlier install,
# or create a new random one.
# -TimeZone: club time zone for time-of-day tariff rules and package windows (IANA or Windows id,
# default: the zone of an earlier install, else Europe/Kyiv).

param(
    [int]$HttpPort = 5081,
    [int]$DiscoveryPort = 5082,
    [string]$InstallPath = "C:\ClubServer",
    [string]$DataPath = (Join-Path $env:ProgramData "PCClub"),
    [string]$PostgresConnectionString = "",
    [string]$EnrollmentToken = "",
    [string]$TimeZone = ""
)

$ErrorActionPreference = 'Stop'

function Test-IsAdmin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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

if (-not (Test-IsAdmin)) {
    Write-Error "Please run this script as Administrator."
    exit 1
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Club Management System - Server Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Project by Volodymyr Shabat" -ForegroundColor DarkGray
Write-Host ""

# Step 1: Create installation directory. C:\ClubServer and C:\ProgramData\PCClub are in folders where every user may
# create a folder: they must not be a junction or a folder of another user (the server runs as LocalSystem). The program
# folder: only SYSTEM and Administrators may change it (it inherited "Authenticated Users: Modify" from C:\ before).
Write-Host "[1/5] Creating installation directory..." -ForegroundColor Yellow
$trustedOwners = Get-TrustedOwners
try {
    Initialize-ProtectedFolder $InstallPath @("*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-32-545:(OI)(CI)RX") $trustedOwners
    Initialize-ProtectedFolder $DataPath @("*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F") $trustedOwners
    # An older install left the program folder open to every user: no junction inside it may take the copy elsewhere.
    # It gets the final permissions before the copy, then it is checked again (nobody can add a junction after that).
    foreach ($round in 1..2) {
        foreach ($folder in @($InstallPath, $DataPath)) {
            $inside = Find-ReparsePoint $folder
            if ($inside) { throw (Get-LinkAdvice $inside) }
        }
        if ($round -eq 1) {
            icacls $InstallPath /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not restrict the permissions of $InstallPath" }
        }
    }
} catch {
    # Windows re-test 2026-11-27 late, LOW 8: new, empty folders made in this step can stay, so not "Nothing was changed"
    Write-Host ("ERROR: $($_.Exception.Message.TrimEnd('.')). The server was not installed or changed. Only new, empty " +
        "folders or stricter permissions of $InstallPath and $DataPath can stay.") -ForegroundColor Red
    exit 1
}

$appsettings = Join-Path $InstallPath "appsettings.json"

# Keep the enrollment token (the club PCs were installed with it) and the time zone of an earlier install.
# $tokenIsNew: this run made the token, so the end of the run shows it once (the staff do not know it yet).
$tokenIsNew = $false
if (Test-Path $appsettings) {
    try {
        $previous = Get-Content $appsettings -Raw | ConvertFrom-Json
        if (-not $EnrollmentToken -and $previous.Devices.EnrollmentToken) { $EnrollmentToken = $previous.Devices.EnrollmentToken }
        if (-not $TimeZone -and $previous.Club.TimeZone) { $TimeZone = $previous.Club.TimeZone }
    } catch {}
}
if (-not $TimeZone) { $TimeZone = "Europe/Kyiv" }
if (-not $EnrollmentToken) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $EnrollmentToken = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $tokenIsNew = $true
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
    Club = @{ TimeZone = $TimeZone }
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
# Restart actions: when the server ends with an error, Windows starts it again after 5 s, 5 s, then 30 s (the count
# goes back to zero after one day). failureflag 1: also when the service stops itself with an error code.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set the restart actions of the service $serviceName (sc.exe failure)" }
sc.exe failureflag $serviceName 1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set the failure flag of the service $serviceName (sc.exe failureflag)" }
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
if ($tokenIsNew) {
    Write-Host "  ClubClientSetup.exe -ServerUrl `"http://<this-server-or-router>:$HttpPort`" -EnrollmentToken `"$EnrollmentToken`"" -ForegroundColor Cyan
    Write-Host "  This run made the enrollment token. Keep it secret. Without it a PC waits for approval in the admin UI (Devices)." -ForegroundColor Gray
} else {
    # Windows re-test 2026-11-27 late, LOW 7: an update printed -EnrollmentToken "" and staff could think that the token
    # was empty. A token that staff know already (kept, or given with -EnrollmentToken) is never shown again.
    Write-Host "  ClubClientSetup.exe -ServerUrl `"http://<this-server-or-router>:$HttpPort`" -EnrollmentToken `"<enrollment token>`"" -ForegroundColor Cyan
    Write-Host "  The enrollment token did not change. It is Devices:EnrollmentToken in $appsettings (Administrators only)." -ForegroundColor Gray
    Write-Host "  Keep it secret. Without it a PC waits for approval in the admin UI (Devices)." -ForegroundColor Gray
}
Write-Host "Then set the staff PIN for the lock screens in the admin UI (Settings)." -ForegroundColor White
Write-Host "Firewall rules have been created for ports $HttpPort and $DiscoveryPort." -ForegroundColor White
Write-Host ""
