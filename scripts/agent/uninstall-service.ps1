$ErrorActionPreference = 'Stop'

param(
    [string]$ServiceName = 'ClubAgent'
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

Write-Host "Uninstalling service '$ServiceName'" -ForegroundColor Cyan

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    try { sc.exe stop $ServiceName | Out-Null } catch {}
    try { sc.exe delete $ServiceName | Out-Null } catch {}
    Write-Host "Service '$ServiceName' removed." -ForegroundColor Green
} else {
    Write-Host "Service '$ServiceName' not found." -ForegroundColor Yellow
}

