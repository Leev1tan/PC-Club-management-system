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
        # Get-LocalGroupMember fails on a group with an orphaned member: then only the accounts above count
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

# Deletes a folder and its content without entering a reparse point: a junction or a link inside is removed as a link.
function Remove-TreeNoFollow([string]$Path) {
    foreach ($entry in [IO.Directory]::GetFileSystemEntries($Path)) {
        $attributes = [IO.File]::GetAttributes($entry)
        $isFolder = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            if ($isFolder) { [IO.Directory]::Delete($entry, $false) } else { [IO.File]::Delete($entry) }
        } elseif ($isFolder) {
            Remove-TreeNoFollow $entry
        } else {
            if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) { [IO.File]::SetAttributes($entry, [IO.FileAttributes]::Normal) }
            [IO.File]::Delete($entry)
        }
    }
    [IO.Directory]::Delete($Path, $false)
}

# Removes a folder (a staged or backed-up version, a leftover of an earlier version, a profile). Refused with a warning
# when Get-FolderProblem finds a problem. True when the folder is gone.
function Remove-FolderSafely([string]$Path, [string[]]$TrustedOwners) {
    $problem = Get-FolderProblem $Path $TrustedOwners
    if ($problem) {
        Write-Warning "Not removed: $Path ($problem). Check it and remove it by hand."
        return $false
    }
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    try {
        Remove-TreeNoFollow $Path
        return $true
    } catch {
        Write-Warning "Could not remove ${Path}: $($_.Exception.Message)"
        return $false
    }
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
    if ($problem) { throw "$Path cannot be used: $problem. A player may have prepared it; check it and remove it by hand" }
    if (-not (Test-Path -LiteralPath $Path)) {
        $security = New-FolderSecurity $Grants
        if ([IO.Directory].GetMethod('CreateDirectory', [Type[]]@([string], [System.Security.AccessControl.DirectorySecurity]))) {
            [IO.Directory]::CreateDirectory($Path, $security) | Out-Null                        # Windows PowerShell 5.1
        } else {
            [IO.FileSystemAclExtensions]::Create((New-Object IO.DirectoryInfo($Path)), $security) # PowerShell 7
        }
    }
    $problem = Get-FolderProblem $Path $TrustedOwners
    if ($problem) { throw "$Path cannot be used: $problem" }
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
    # An older install left the program folder open to every user: no junction inside it may take the copy elsewhere
    foreach ($folder in @($InstallPath, $DataPath)) {
        $inside = Find-ReparsePoint $folder
        if ($inside) { throw "$inside is a junction or a symbolic link; check it and remove it by hand" }
    }
} catch {
    Write-Host "ERROR: $($_.Exception.Message). Nothing was changed." -ForegroundColor Red
    exit 1
}
icacls $InstallPath /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not restrict the permissions of $InstallPath" }

$appsettings = Join-Path $InstallPath "appsettings.json"

# Keep the enrollment token (the club PCs were installed with it) and the time zone of an earlier install
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
Write-Host "  ClubClientSetup.exe -ServerUrl `"http://<this-server-or-router>:$HttpPort`" -EnrollmentToken `"$EnrollmentToken`"" -ForegroundColor Cyan
Write-Host "  Keep the token secret. Without it a PC waits for approval in the admin UI (Devices)." -ForegroundColor Gray
Write-Host "Then set the staff PIN for the lock screens in the admin UI (Settings)." -ForegroundColor White
Write-Host "Firewall rules have been created for ports $HttpPort and $DiscoveryPort." -ForegroundColor White
Write-Host ""
