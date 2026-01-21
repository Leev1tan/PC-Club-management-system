# MVP Integration Test Checklist

## Prerequisites
- [ ] PostgreSQL installed or Docker running: `docker run --name postgres-cms -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres`
- [ ] VM with Windows 11 accessible (192.168.0.X or similar)

## Setup Steps

### 1. Start Server (Host PC)
```powershell
# Terminal 1 - Start server
cd F:\v.wae\club-management-system
dotnet run --project Cms.Server/Cms.Server.csproj --urls=http://0.0.0.0:5081
```
- [ ] Server starts
- [ ] Auto-migration runs (check console for "Applied migration")
- [ ] Swagger at http://localhost:5081/swagger works

### 2. Start Admin UI (Host PC)
```powershell
# Terminal 2 - Start admin UI
cd F:\v.wae\club-management-system\admin-ui
npm run dev
```
- [ ] Admin UI at http://localhost:3000 loads
- [ ] Shows "No devices registered yet"

### 3. Deploy to VM
On host PC:
```powershell
# Terminal 3 - Deploy agent and launcher (run from VM or copy files manually)
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-to-vm.ps1 -VMPath C:\win-x64 -LauncherPath C:\ClubLauncher
```
Or manually:
- [ ] Copy `publish\agent\win-x64\*` to `C:\win-x64\` on VM
- [ ] Copy `publish\launcher\win-x64\*` to `C:\ClubLauncher\` on VM
- [ ] Ensure `C:\win-x64\appsettings.json` has correct server IP (e.g., `"BaseUrl": "http://192.168.0.130:5081"`)

On VM (Admin PowerShell):
```powershell
# Install service
sc.exe stop ClubAgent
sc.exe delete ClubAgent
sc.exe create ClubAgent binPath= "C:\win-x64\Cms.Agent.Service.exe" start= auto obj= "NT AUTHORITY\LocalService" type= own
sc.exe start ClubAgent
sc.exe query ClubAgent
```
- [ ] Service shows RUNNING

### 4. Start Launcher on VM
On VM (as normal user):
```powershell
C:\ClubLauncher\Cms.Launcher.exe
```
- [ ] Launcher window opens full-screen
- [ ] Shows "Club Launcher" and "Time remaining: 00:00:00"

## Tests

### Test 1: Device Registration & Heartbeat
- [ ] Admin UI refreshes and shows the VM device
- [ ] Device status = "online"
- [ ] Last seen updates every ~5 seconds
- [ ] Hostname, OS, agent version displayed

### Test 2: Lock/Unlock
From admin UI, click device's "Lock" button:
- [ ] VM launcher shows dark overlay "This PC is locked"
- [ ] Windows key blocked on VM
- [ ] Alt+Tab blocked on VM

From admin UI, click "Unlock":
- [ ] Overlay disappears on VM
- [ ] Windows key works again

### Test 3: Staff Unlock (Ctrl+Shift+U)
Lock the VM via admin UI, then on VM:
- [ ] Press Ctrl+Shift+U
- [ ] PIN prompt appears
- [ ] Enter "1234"
- [ ] Overlay drops immediately

### Test 4: Session Start & Countdown
From admin UI, click "Start Session" on the device:
- [ ] Prompt for duration; enter "2" (2 minutes)
- [ ] Session starts
- [ ] VM launcher main UI shows "Time remaining: 00:02:00" and counts down
- [ ] Device unlocks if it was locked

### Test 5: Auto-Lock on Session End
Wait for countdown to reach 00:00:00:
- [ ] Overlay appears automatically
- [ ] Shows "Time remaining: 00:00:00" on overlay
- [ ] Keys blocked

### Test 6: Restart Command
From admin UI, click "Restart" on device:
- [ ] VM restarts after ~5 seconds
- [ ] After reboot, service auto-starts
- [ ] Device re-registers and appears online in admin UI

### Test 7: Fail-Safe (Service Down)
On VM, stop the service:
```powershell
sc.exe stop ClubAgent
```
Wait 15+ seconds:
- [ ] Launcher overlay drops (fail-open due to stale heartbeat)
- [ ] Keys unblocked

Restart service:
```powershell
sc.exe start ClubAgent
```
- [ ] Device reappears in admin UI

### Test 8: Autostart Launcher
On VM:
```powershell
powershell -ExecutionPolicy Bypass -File C:\path\to\scripts\install-launcher-autostart.ps1 -LauncherPath C:\ClubLauncher\Cms.Launcher.exe
```
Log off and log back in:
- [ ] Launcher starts automatically

## Known Issues / Debug
- If device doesn't appear: check `C:\ProgramData\ClubAgent\agent_heartbeat.txt` timestamp updates
- If lock doesn't work: check `C:\ProgramData\ClubAgent\state.json` shows `"isLocked":true`
- If countdown doesn't show: check `state.json` has `"remainingSeconds"` > 0
- If keys not blocked: ensure launcher has focus or is topmost

## Success Criteria
All tests pass. Device registers, heartbeats, lock/unlock works, sessions count down and auto-lock at zero.

---

## MVP Complete! 🎉
If all tests pass, the core system is functional. Next steps:
- Polish UI
- Add user/member management
- POS integration
- Multi-venue support
- Game platform integrations

