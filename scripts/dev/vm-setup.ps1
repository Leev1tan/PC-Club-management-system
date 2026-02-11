<#
.SYNOPSIS
    One-time setup script to enable remote deployment on a VM.

.DESCRIPTION
    Run this script ONCE on each VM (as Administrator) to enable:
    - PowerShell Remoting (WinRM)
    - Admin share access (C$)
    - Firewall rules

.NOTES
    After running this, you can deploy from host using: .\deploy.ps1
#>

$ErrorActionPreference = 'Stop'

Write-Host "=== CMS VM Setup ===" -ForegroundColor Cyan
Write-Host "This enables remote deployment. Run as Administrator.`n" -ForegroundColor Gray

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: Run this script as Administrator!" -ForegroundColor Red
    exit 1
}

# 1. Enable PowerShell Remoting
Write-Host "[1/4] Enabling PowerShell Remoting..." -ForegroundColor Yellow
Enable-PSRemoting -Force -SkipNetworkProfileCheck
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "*" -Force
Write-Host "  Done" -ForegroundColor Green

# 2. Configure WinRM for unencrypted (local network only)
Write-Host "[2/4] Configuring WinRM..." -ForegroundColor Yellow
Set-Item WSMan:\localhost\Service\AllowUnencrypted -Value $true -Force
Set-Item WSMan:\localhost\Service\Auth\Basic -Value $true -Force
Write-Host "  Done" -ForegroundColor Green

# 3. Enable admin shares (C$)
Write-Host "[3/4] Enabling admin shares..." -ForegroundColor Yellow
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "LocalAccountTokenFilterPolicy" -Value 1 -Type DWord -Force
Write-Host "  Done" -ForegroundColor Green

# 4. Add firewall rules
Write-Host "[4/4] Configuring firewall..." -ForegroundColor Yellow
$rules = @(
    @{ Name = "WinRM-HTTP-In"; Port = 5985; Protocol = "TCP" },
    @{ Name = "SMB-In"; Port = 445; Protocol = "TCP" }
)
foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
    if (-not $existing) {
        New-NetFirewallRule -Name $rule.Name -DisplayName $rule.Name -Protocol $rule.Protocol -LocalPort $rule.Port -Action Allow -Direction Inbound | Out-Null
        Write-Host "  Created rule: $($rule.Name)" -ForegroundColor Gray
    } else {
        Write-Host "  Rule exists: $($rule.Name)" -ForegroundColor DarkGray
    }
}
Write-Host "  Done" -ForegroundColor Green

# Show IP for reference
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notmatch 'Loopback' } | Select-Object -First 1).IPAddress
Write-Host "`n=== Setup Complete ===" -ForegroundColor Cyan
Write-Host "VM IP: $ip" -ForegroundColor White
Write-Host "`nOn host, update scripts/dev/vm-config.ps1 with:" -ForegroundColor Gray
Write-Host "  VMIp = `"$ip`"" -ForegroundColor Yellow
Write-Host "`nThen run: .\scripts\dev\deploy.ps1" -ForegroundColor Gray
