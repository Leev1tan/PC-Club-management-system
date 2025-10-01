param(
    [string]$VMPath = 'C:\win-x64',
    [string]$LauncherPath = 'C:\ClubLauncher'
)

$ErrorActionPreference = 'Stop'

Write-Host "Publishing agent and launcher..." -ForegroundColor Cyan

# Publish agent
dotnet publish .\Cms.Agent.Service\Cms.Agent.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\agent\win-x64 | Out-Null

# Publish launcher
dotnet publish .\Cms.Launcher\Cms.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\launcher\win-x64 | Out-Null

Write-Host "Stopping service..." -ForegroundColor Yellow
try { sc.exe stop ClubAgent | Out-Null } catch {}
Start-Sleep -Seconds 2

Write-Host "Copying files to $VMPath and $LauncherPath..." -ForegroundColor Cyan
Copy-Item -Path .\publish\agent\win-x64\* -Destination $VMPath -Force -Recurse
Copy-Item -Path .\publish\launcher\win-x64\* -Destination $LauncherPath -Force -Recurse

Write-Host "Starting service..." -ForegroundColor Green
sc.exe start ClubAgent | Out-Null

Write-Host "Deploy complete. Launcher at $LauncherPath\Cms.Launcher.exe" -ForegroundColor Green

