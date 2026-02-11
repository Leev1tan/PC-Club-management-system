# VM Deployment Configuration
# Edit these values to match your environment

$script:VMConfig = @{
    # VM Network Settings
    VMIp = "192.168.0.100"  # <-- Change to your VM's IP address
    VMUser = "User"          # <-- Windows username on VM (usually "User" or your account name)
    
    # Paths on VM
    AgentPath = "C:\win-x64"
    LauncherPath = "C:\ClubLauncher"
    
    # VMware Shared Folder (if using)
    # Enable in VM Settings > Options > Shared Folders
    # The publish folder will be accessible at: \\vmware-host\Shared\publish
    UseSharedFolder = $false  # Set to $true if you configure VMware shared folders
    
    # Server URL (what the agent connects to)
    # Leave empty to use auto-discovery, or set explicitly
    ServerUrl = ""  # e.g., "http://192.168.0.130:5081"
}

# Credential handling - will prompt on first use, then cache securely
function Get-VMCredential {
    $credPath = "$env:USERPROFILE\.cms-vm-cred.xml"
    if (Test-Path $credPath) {
        try {
            return Import-Clixml $credPath
        } catch {
            Remove-Item $credPath -Force
        }
    }
    
    Write-Host "Enter credentials for VM ($($script:VMConfig.VMIp)):" -ForegroundColor Yellow
    $cred = Get-Credential -UserName $script:VMConfig.VMUser -Message "VM Login"
    $cred | Export-Clixml $credPath
    return $cred
}

# Export config
Export-ModuleMember -Variable VMConfig -Function Get-VMCredential
