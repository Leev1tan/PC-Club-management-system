# Builds the Windows release packages. .github/workflows/release.yml runs this script, so a local
# run gives the same files. It also runs on Linux for a check (cross-compile; pwsh is in the .NET SDK image).
#
#   ccms-server-<version>-win-x64.zip    server\ (Cms.Server.exe, admin UI in wwwroot), install-server.ps1, Install.cmd
#   ccms-agent-<version>-win-x64.zip     agent\ (Cms.Agent.Service.exe), install-client.ps1, uninstall-kiosk.ps1, Install.cmd
#   ccms-launcher-<version>-win-x64.zip  launcher\ (Cms.Launcher.exe)
#   SHA256SUMS                           "<sha256>  <file>" lines (sha256sum -c SHA256SUMS)
# Every zip has LICENSE, NOTICE.md and INSTALL.txt. The publish commands are the same as in
# scripts/installer/build-packages.ps1 (self-contained win-x64, single file).
#
# Build the admin UI first:  cd admin-ui; npm ci; npm run build   (static files in admin-ui/out)
# Usage: pwsh scripts/release/build-release.ps1 -Version v1.2.3 [-OutDir dist] [-AdminUiOut admin-ui/out]

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir = 'dist',
    [string]$AdminUiOut = 'admin-ui/out'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repoRoot

# v1.2.3 or v1.2.3-rc.1 (a tag), or a dry-run name such as dryrun-1a2b3c4 (assembly version 0.0.0)
if ($Version -match '^v(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') {
    $numeric = $Matches[1] + $Matches[2]
} elseif ($Version -match '^[0-9A-Za-z][0-9A-Za-z.-]{0,40}$') {
    $numeric = '0.0.0'
} else {
    throw "Version must look like v1.2.3 (or a dry-run name of letters, digits, '.' and '-'): $Version"
}

$adminUi = Join-Path $repoRoot $AdminUiOut
if (!(Test-Path (Join-Path $adminUi 'index.html'))) {
    throw "The admin UI is not built ($AdminUiOut/index.html is missing). Run: cd admin-ui; npm ci; npm run build"
}

$out = Join-Path $repoRoot $OutDir
$work = Join-Path $out '.work'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Invoke-Native([string]$What, [scriptblock]$Command) {
    Write-Host "== $What" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}

# On Linux and macOS the Windows projects need EnableWindowsTargeting (cross-compile only)
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', "-p:Version=$numeric", '-nologo')
if (-not $IsWindows) { $common += '-p:EnableWindowsTargeting=true' }

$serverOut = Join-Path $work 'server'
$agentOut = Join-Path $work 'agent'
$launcherOut = Join-Path $work 'launcher'

Invoke-Native 'Publish Cms.Server (win-x64)' {
    dotnet publish Cms.Server/Cms.Server.csproj @common -p:IncludeNativeLibrariesForSelfExtract=true -o $serverOut
}
Invoke-Native 'Publish Cms.Agent.Service (win-x64)' {
    dotnet publish Cms.Agent.Service/Cms.Agent.Service.csproj @common -p:IncludeNativeLibrariesForSelfExtract=true -o $agentOut
}
Invoke-Native 'Publish Cms.Launcher (win-x64)' {
    dotnet publish Cms.Launcher/Cms.Launcher.csproj @common -o $launcherOut
}

# The Windows server serves the admin UI from wwwroot (same origin as the API)
$wwwroot = Join-Path $serverOut 'wwwroot'
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Copy-Item -Path (Join-Path $adminUi '*') -Destination $wwwroot -Recurse -Force

# Debug symbols are not shipped
Get-ChildItem -Path $work -Recurse -Filter '*.pdb' | Remove-Item -Force

function New-InstallCmd([string]$Path, [string]$Script) {
    $text = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0$Script`" %*`r`nif errorlevel 1 pause`r`n"
    [IO.File]::WriteAllText($Path, $text, [Text.Encoding]::ASCII)
}

function New-Package([string]$Name, [string]$InstallText, [hashtable]$Folders, [string[]]$Scripts, [string]$InstallCmdScript) {
    $stage = Join-Path $work "stage-$Name"
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($folder in $Folders.Keys) {
        Copy-Item -Path $Folders[$folder] -Destination (Join-Path $stage $folder) -Recurse -Force
    }
    foreach ($script in $Scripts) { Copy-Item -Path $script -Destination $stage -Force }
    if ($InstallCmdScript) { New-InstallCmd (Join-Path $stage 'Install.cmd') $InstallCmdScript }
    Copy-Item -Path (Join-Path $repoRoot 'LICENSE') -Destination $stage -Force
    Copy-Item -Path (Join-Path $repoRoot 'NOTICE.md') -Destination $stage -Force
    $install = (Get-Content -Raw -Path (Join-Path $PSScriptRoot $InstallText)).Replace('{VERSION}', $Version)
    [IO.File]::WriteAllText((Join-Path $stage 'INSTALL.txt'), $install.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.UTF8Encoding]::new($false))

    $zip = Join-Path $out "ccms-$Name-$Version-win-x64.zip"
    # ZipFile on .NET 8 writes '/' separators on every platform
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "   $zip" -ForegroundColor Gray
}

Write-Host '== Packages' -ForegroundColor Cyan
New-Package -Name 'server' -InstallText 'INSTALL-server.txt' -Folders @{ server = $serverOut } `
    -Scripts @('scripts/installer/install-server.ps1') -InstallCmdScript 'install-server.ps1'
New-Package -Name 'agent' -InstallText 'INSTALL-agent.txt' -Folders @{ agent = $agentOut } `
    -Scripts @('scripts/installer/install-client.ps1', 'scripts/installer/uninstall-kiosk.ps1') -InstallCmdScript 'install-client.ps1'
New-Package -Name 'launcher' -InstallText 'INSTALL-launcher.txt' -Folders @{ launcher = $launcherOut } -Scripts @() -InstallCmdScript ''

Remove-Item $work -Recurse -Force

# SHA256SUMS in the format of sha256sum (lower-case hex, two spaces, LF line ends)
$lines = Get-ChildItem -Path $out -Filter '*.zip' | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
}
[IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS'), (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Get-Content (Join-Path $out 'SHA256SUMS')
