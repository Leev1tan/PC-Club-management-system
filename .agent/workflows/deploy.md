---
description: How to deploy and test the PC Club system on VMs
---

# Deploy & Test on VM

## Architecture

```
┌─────────────────────────────┐    ┌─────────────────────────────┐
│   SERVER (your PC / VM-1)   │    │   CLIENT (VM-2, VM-3, ...)  │
│                             │    │                             │
│  Cms.Server    → :5081 API  │◄──►│  ClubAgent (Windows Svc)    │
│  admin-ui      → :3000 UI  │    │  Cms.Launcher (auto-start)  │
└─────────────────────────────┘    └─────────────────────────────┘
```

## Step 1: Build installer packages (on dev PC)

// turbo
```powershell
powershell -File f:\v.wae\club-management-system\scripts\installer\build-packages.ps1
```

This produces self-contained single-file EXEs (no .NET needed on VMs):
- `publish/installers/server-installer.zip`
- `publish/installers/client-installer.zip`

## Step 2: Deploy to server machine

1. Copy `server-installer.zip` → extract on server
2. Run as Admin:
```powershell
.\install-server.ps1
```

## Step 3: Deploy to each client VM

1. Copy `client-installer.zip` → extract on VM
2. Run as Admin:
```powershell
.\install-client.ps1 -ServerUrl "http://<SERVER_IP>:5081"
```

This will:
- Install Agent to `C:\ClubAgent\` and register as **Windows Service** (auto-start)
- Install Launcher to `C:\ClubLauncher\` and add to **Common Startup** (opens on login)
- No .NET runtime needed (self-contained build)

Skip `-ServerUrl` to use UDP auto-discovery (same LAN only).

## Step 4: Admin UI (on server, needs Node.js)

```powershell
cd admin-ui
set NEXT_PUBLIC_API_URL=http://localhost:5081
npm run build && npm run start
# → http://localhost:3000
```

## Step 5: Verify

1. Open **Admin UI** at `http://<SERVER_IP>:3000`
2. VM should appear in **Devices** tab within ~5s
3. Try: Lock/Unlock, Start session, Staff unlock (Ctrl+Shift+U)

## Firewall

- **TCP 5081** — API (required)
- **TCP 3000** — Admin UI
- **UDP 5082** — Auto-discovery (optional)

## Quick dev test (single machine, no install)

// turbo-all
```powershell
# Terminal 1: Server
dotnet run --project Cms.Server

# Terminal 2: Admin UI
cd admin-ui && npm run dev

# Terminal 3: Agent
dotnet run --project Cms.Agent.Service

# Terminal 4: Launcher
dotnet run --project Cms.Launcher
```
