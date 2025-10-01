param(
    [string]$Target,
    [switch]$Publish,
    [string]$Runtime
)

if (-not $Target) { $Target = 'all' }
if (-not $Runtime) { $Runtime = 'win-x64' }

$ErrorActionPreference = 'Stop'

function Build($proj) {
    dotnet build $proj -c Release | Out-Host
}
function Publish-Project($proj, $outPath) {
    dotnet publish $proj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -o $outPath | Out-Host
}
function RunServer() {
    Start-Process -FilePath dotnet -ArgumentList 'run --project .\Cms.Server\Cms.Server.csproj --urls=http://0.0.0.0:5081' -WindowStyle Hidden | Out-Null
    Write-Host 'Server started on http://0.0.0.0:5081' -ForegroundColor Green
}
function RunLauncher() {
    Start-Process -FilePath dotnet -ArgumentList 'run --project .\Cms.Launcher\Cms.Launcher.csproj' -WorkingDirectory (Resolve-Path '.') | Out-Null
}

switch ($Target.ToLowerInvariant()) {
    'server' {
        if ($Publish) { Publish-Project '.\Cms.Server\Cms.Server.csproj' '.\publish\server' } else { Build '.\Cms.Server\Cms.Server.csproj'; RunServer }
    }
    'agent' {
        if ($Publish) { Publish-Project '.\Cms.Agent.Service\Cms.Agent.Service.csproj' '.\publish\agent\win-x64' } else { Build '.\Cms.Agent.Service\Cms.Agent.Service.csproj' }
    }
    'launcher' {
        if ($Publish) { Publish-Project '.\Cms.Launcher\Cms.Launcher.csproj' '.\publish\launcher\win-x64' } else { Build '.\Cms.Launcher\Cms.Launcher.csproj'; RunLauncher }
    }
    'all' {
        Build '.\Cms.Server\Cms.Server.csproj'
        Build '.\Cms.Agent.Service\Cms.Agent.Service.csproj'
        Build '.\Cms.Launcher\Cms.Launcher.csproj'
        RunServer
        RunLauncher
    }
    default {
        Write-Error "Unknown target '$Target'. Use server|agent|launcher|all."
        exit 1
    }
}

