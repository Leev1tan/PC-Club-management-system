$ErrorActionPreference = 'Stop'

param(
    [string]$ServiceName = 'ClubAgent',
    [string]$BinaryPath = ''
)

function Test-IsAdmin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    Write-Error 'Please run this script as Administrator.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($BinaryPath)) {
    $BinaryPath = Join-Path $PSScriptRoot 'Cms.Agent.Service.exe'
}

if (-not (Test-Path $BinaryPath)) {
    Write-Error "Service binary not found: $BinaryPath"
    exit 1
}

Write-Host "Installing service '$ServiceName' -> $BinaryPath" -ForegroundColor Cyan

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    try { sc.exe stop $ServiceName | Out-Null } catch {}
    try { sc.exe delete $ServiceName | Out-Null } catch {}
}

$escaped = '"' + $BinaryPath + '"'
sc.exe create $ServiceName binPath= $escaped start= auto obj= 'NT AUTHORITY\LocalService' type= own | Out-Null
sc.exe description $ServiceName 'Cybersport club agent' | Out-Null
sc.exe start $ServiceName | Out-Null

Write-Host "Service '$ServiceName' installed and started." -ForegroundColor Green

