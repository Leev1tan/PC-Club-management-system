# Club Deployment Guide

Project authorship: created and maintained by Volodymyr Shabat.

This guide is for a club network where gaming PCs are on `10.3.3.100-255`, the MikroTik router is `10.3.3.1`, and switch ports isolate PCs from each other.

The gaming PCs are Windows 10/11 machines: games, the agent (`ClubAgent` service) and the lock-screen launcher run there. For the server there are two options. Both serve the admin UI and the API on the same URL (`http://<server>:5081`), so the router rules and the client install below are the same for both.

| | Option A: Windows PC in the club (installer) | Option B: Docker compose (Linux, or Windows with Docker) |
|---|---|---|
| Install | `ClubServerSetup.exe` | `docker compose up -d` in the repository folder |
| Runs as | Windows service `ClubServer` | containers `ccms-postgres-1`, `ccms-server-1`, `ccms-admin-ui-1` |
| Database | SQLite in `C:\ProgramData\PCClub\cms.db` (default) or PostgreSQL | PostgreSQL 16 container, volume `ccms-pgdata` |
| First Owner | setup code printed by the installer | setup code: `docker compose exec server cat /app/data/setup-code.txt`, or `CMS_INITIAL_OWNER_*` in `.env` |
| Enrollment token for the PCs | printed by the installer (kept on reinstall) | `CMS_ENROLLMENT_TOKEN` in `.env` |
| Port | TCP 5081 (+ UDP 5082 for discovery) | TCP 5081 (no UDP discovery) |
| Clients | `ClubClientSetup.exe -ServerUrl ... -EnrollmentToken ...` | `ClubClientSetup.exe -ServerUrl ... -EnrollmentToken ...` |

## Network Model

- Core control must use client-to-server HTTP polling.
- Do not depend on LAN auto-discovery in this topology.
- Do not depend on PC-to-PC traffic.
- Use the router as the stable endpoint for all clients and admin browsers.

Recommended public endpoint inside the club:

```text
http://10.3.3.1:5081
```

Example fixed server PC address:

```text
10.3.3.100
```

## MikroTik Rules

Forward the router endpoint to the server PC:

```routeros
/ip firewall nat
add chain=dstnat dst-address=10.3.3.1 protocol=tcp dst-port=5081 action=dst-nat to-addresses=10.3.3.100 to-ports=5081 comment="ClubServer API/UI"
add chain=srcnat src-address=10.3.3.0/24 dst-address=10.3.3.100 protocol=tcp dst-port=5081 action=masquerade comment="ClubServer hairpin NAT"
```

Notes:

- Replace `10.3.3.100` with the real ClubServer PC IP.
- The hairpin masquerade rule is important when clients and server are in the same subnet but switch isolation blocks direct traffic.
- Server-side remote IP may look like the router IP. Device identity still comes from each agent's device ID, hostname, and heartbeat payload.

## Install from a release

A GitHub release (tag `v1.2.3`) has ready Windows packages: `ccms-server-<tag>-win-x64.zip`,
`ccms-agent-<tag>-win-x64.zip`, `ccms-launcher-<tag>-win-x64.zip` and `SHA256SUMS`. They are
self-contained (no .NET needed on the PCs) and not code-signed.

1. Check every zip before you use it: `Get-FileHash <zip>` (PowerShell) must give the SHA-256 that
   `SHA256SUMS` lists for that file.
2. Server PC (option A): extract the server zip and run `Install.cmd` as Administrator. It takes
   the same options as `ClubServerSetup.exe` below (`-HttpPort`, `-DataPath`, `-EnrollmentToken`,
   `-TimeZone`, `-PostgresConnectionString`).
3. Gaming PCs: extract the agent zip and the launcher zip into the same folder (it then has
   `agent\`, `launcher\` and `Install.cmd`) and run as Administrator:

   ```powershell
   Install.cmd -ServerUrl "http://10.3.3.1:5081" -EnrollmentToken "<token>" -KioskMode
   ```

   This is the same installer as `ClubClientSetup.exe`, see [Client Install](#client-install).
4. Update: extract the zips of the new version into a new folder and run `Install.cmd` again with
   the same options. The server keeps its data, enrollment token and time zone. On a gaming PC, update
   when no session runs. The client installer prepares first (the new files and a backup of the old ones in a
   new private folder `ccms-update-<random>` in the temp folder of the administrator or of SYSTEM, which only
   SYSTEM and Administrators can open), then stops the agent and the lock screen (also its watchdog), copies
   and starts the agent. The installers never use, change or delete a folder that is a junction or a symbolic
   link or that a player owns; then they stop with a message and change nothing. If the copy or the start fails, it
   restores the old version, starts it again and ends with exit code 1. On success it signs out ClubUser.
   Windows 11 does not sign ClubUser in again after a sign-out: on a kiosk PC the agent restarts the PC about
   2 minutes after the last user signs out, and ClubUser signs in with the new lock screen (see Kiosk mode). The
   installer never restarts the PC itself. The last line says `RESULT: OK` or `RESULT: FAILED`. The installer keeps the `Teardown` and `AccountPool` settings of
   `C:\ClubAgent\appsettings.json`, and on a registered PC it does not store the enrollment token.

Option B (Docker) does not use these files; CI publishes the images to `ghcr.io` for the same tags.

## Server Install

### Option A: Windows PC (installer)

Run on the ClubServer PC as Administrator (`ClubServerSetup.exe` from `scripts\installer\build-packages.ps1`,
or `Install.cmd` of the release server zip):

```powershell
ClubServerSetup.exe
```

The installer:

- keeps the data in `C:\ProgramData\PCClub` (database `cms.db`, login keys, setup code); only SYSTEM and
  Administrators can open this folder. A `cms.db` of an earlier version (for example in
  `C:\Windows\System32`) is copied there on the first start; the old file stays until you delete it.
- creates an enrollment token for the club PCs (or keeps the one of an earlier install) and prints it.
- prints the one-time setup code for the first Owner account.
- sets the club time zone for tariff rules and packages (`-TimeZone`, default Europe/Kyiv; an earlier
  install keeps its value). Docker: `CMS_TIMEZONE` in `.env`.

Check locally:

```powershell
Get-Service ClubServer
Invoke-RestMethod http://localhost:5081/health
```

Expected:

```text
ClubServer is Running
health returns status healthy
```

### Option B: Docker compose

On the server (Linux with Docker Engine and the compose plugin, or Windows with Docker Desktop), in the repository folder:

```bash
cp .env.example .env      # set POSTGRES_PASSWORD and CMS_ENROLLMENT_TOKEN (openssl rand -base64 24)
docker compose up -d
docker compose exec server cat /app/data/setup-code.txt   # one-time code for the first Owner
```

Check locally:

```bash
docker compose ps
curl http://localhost:5081/api/health
```

Expected:

```text
three containers are running (postgres and server show "healthy")
health returns status healthy
```

Notes:

- Port 5081 serves the admin UI and proxies `/api` to the server container, so the MikroTik rules above stay the same (use the Docker host IP as `10.3.3.100`).
- UDP discovery does not work from the container network. Always install clients with `-ServerUrl`.
- On a Windows server with two network cards, discovery answers with the address of the card that the request
  came in on (set `Discovery:ServerUrl` to force one address).
- On Windows with Docker Desktop, allow TCP 5081 in Windows Firewall.

## First Start: Owner, Staff, Staff PIN

Do this before you install the club PCs. Nobody can use the admin UI without an account, and the
server never creates a default account.

1. Open `http://<server>:5081`. The setup screen opens. Enter the setup code, a user name and a
   password (at least 8 characters). This creates the Owner and signs you in. The setup screen and the
   code stop working at that moment.
   - Docker alternative: set `CMS_INITIAL_OWNER_USERNAME` and `CMS_INITIAL_OWNER_PASSWORD` in `.env`
     before the first `docker compose up -d`, then remove the password from `.env`.
2. Settings: set the staff PIN of the lock screens (at least 8 characters, digits and letters). The PCs
   receive only its hash. Without a PIN there is no emergency unlock on the PCs.
3. Staff: create one account per employee with the smallest role that fits (Cashier for the front desk,
   Tech for PC maintenance, Manager for tariffs and corrections). Do not share accounts: the audit log
   records who did what.

4. Tariffs: for every zone create a tariff with its base price and time rules (for example
   Mon–Thu 08:00–17:00 cheaper, every day 22:00–08:00 night price), the rounding step and the minimum
   charge; then the packages (3 h, 5 h, night pack 22:00–08:00) and, if you want, cashback tiers on
   top-ups. Owner or Manager only.

If the setup code is lost, restart the server (`Restart-Service ClubServer` or
`docker compose restart server`): it writes a new code while no staff account exists.

## Router Endpoint Test

From the admin PC and from one gaming PC:

```powershell
Test-NetConnection 10.3.3.1 -Port 5081
Invoke-RestMethod http://10.3.3.1:5081/health
```

Expected:

```text
TcpTestSucceeded : True
health returns status healthy
```

Do not continue client rollout until this passes.

## Client Install

Run on each gaming PC as Administrator (`ClubClientSetup.exe`, or `Install.cmd` of the release agent and
launcher zips extracted into one folder):

```powershell
ClubClientSetup.exe -ServerUrl "http://10.3.3.1:5081" -EnrollmentToken "<token>" -KioskMode
```

- With the enrollment token the PC is approved at once. The agent deletes the token file after its first
  successful registration or heartbeat. On a PC that is registered already (an update) the installer does not
  store the token. The token is sent only to the explicit `-ServerUrl`, never to a server found by discovery.
- Without the token the PC appears as "pending" in the admin UI (Devices). Approve it there (Owner,
  Manager or Tech). Reject unknown PCs.
- A reinstalled PC with the same name keeps its record (zone, history) when it registers with the token;
  its old key stops working. Revoke a PC that leaves the club (Devices, revoke button).
- A new PC is locked. A club PC is locked unless a session runs on it or staff unlocked it in the admin UI.
  A staff unlock of a PC without a session ends when the agent or the PC restarts.
- The installer works as Administrator and as SYSTEM (RMM tools, Intune, a VM guest agent). It asks no
  question when it runs without a console.
- The agent never stops because the server is off, the network is down or a request times out: it tries
  again until the server answers. Windows also restarts the service after a failure (restart actions 5 s, 5 s,
  30 s; `sc.exe qfailure ClubAgent`). The server service `ClubServer` has the same restart actions.
- The agent log is `C:\ProgramData\ClubAgent\logs\agent.log` (at most 4 files of 1 MB; Administrators only). It has
  no tokens, keys, PINs, passwords or account secrets. Warnings and errors also go to the Application event log
  (source `Cms.Agent.Service`, registered by the installer).

Check:

```powershell
Get-Service ClubAgent
Get-Content C:\ProgramData\ClubAgent\config.json
Get-Content C:\ProgramData\ClubAgent\state.json
Test-Path C:\ProgramData\ClubAgent\staff-pin.json
icacls C:\ProgramData\ClubAgent\private
```

Expected:

```text
ClubAgent is Running
config.json contains http://10.3.3.1:5081
state.json is created after the agent starts
staff-pin.json exists after the staff PIN is set (True)
private: only SYSTEM, Administrators and LOCAL SERVICE have access
```

More checks: `sc.exe qfailure ClubAgent` lists RESTART actions; `icacls C:\ProgramData\ClubAgent\logs` shows only
SYSTEM, Administrators and LOCAL SERVICE; `Get-Content C:\ProgramData\ClubAgent\logs\agent.log -Tail 20` shows
"ClubAgent ... started"; `C:\ProgramData\ClubAgent\private\enrollment-token.txt` is gone after the first heartbeat.

### Kiosk mode (`-KioskMode`)

- The players use the Windows account `ClubUser`. It signs in automatically, and the lock screen is its shell.
- Windows signs `ClubUser` in automatically (`AutoAdminLogon`) only at the start of the PC, about 1 minute after the
  boot. After a sign-out (the staff "Log off", an update, a sign-out of `ClubAdmin`) Windows 11 shows the Windows
  sign-in screen and does not sign `ClubUser` in again (measured on Windows 11, Windows re-test of 2026-11-28). So the
  agent restarts a kiosk PC (`kiosk.json` names `ClubUser`) on which nobody is signed in:
  - when nobody is signed in for 120 s and Windows runs 5 minutes or more (a start of the PC before the automatic
    sign-in never causes a restart);
  - at once after a staff "Log off", when the session of `ClubUser` has ended;
  - never while a user is signed in: `ClubUser`, `ClubAdmin` or another account, active or disconnected (after
    "Switch user"). One exception: when the only user session left is a disconnected session of `ClubUser` (after
    "Switch user > ClubAdmin" and a sign-out of `ClubAdmin`, or a player's "Switch user") and the PC is locked, that
    counts as nobody: the PC shows the Windows sign-in screen, and nobody can sign in as `ClubUser`. While a player
    session runs (the PC is unlocked) it counts as signed in: a restart would end the paid time; the PC map shows ⚠️
    (the lock screen does not answer), and staff decide (end the session, or "Log off": the PC restarts at once).
    The count of 120 s starts when the last user signs out or when the PC is locked, and starts again when a session
    starts during the count;
  - at most 3 times in 60 minutes. Then it stops, writes the ERROR "Kiosk sign-in fails" to `agent.log` and the
    Application event log, and the PC map of the admin UI shows ⚠️ 🔑 on the PC. Check the automatic sign-in
    (`AutoAdminLogon`, `DefaultUserName`, the task `ClubKioskAutoLogon`), then restart the PC. The record of the
    restarts is `C:\ProgramData\ClubAgent\private\kiosk-restarts.json` (SYSTEM, Administrators and the agent only);
    delete it to allow restarts again before the hour is over.

  `agent.log` has one line for each change: "nobody is signed in ... restarts Windows in 120 s", "signed in: ...",
  "restarting Windows now ...". A PC without kiosk mode is never restarted. The agent (LocalService) enables its
  `SeShutdownPrivilege` for the restart; the installer gives the service no restricted privilege list.
  Known (Windows re-test 2026-11-28 morning, 1 of 2 runs): after a staff "Log off" about 1.5 minutes after a start of
  the PC, the agent asked for the restart at once, but Windows went down only 4 minutes later. No event shows why. The
  other run went down in 11 s. If a PC stays at the sign-in screen longer than 5 minutes, restart it.
- The installer does not set `ForceAutoLogon` and removes it when an older version set it: that value also signed
  `ClubUser` in again on "Switch user", so `ClubAdmin` could never sign in at the screen.
  Windows writes the last user of the screen into `DefaultUserName`. The scheduled task `ClubKioskAutoLogon`
  (SYSTEM: at the start, at every sign-in and every 5 minutes; only `reg.exe` with fixed values) writes the values of
  `ClubUser` again, so `ClubUser` signs in at the next start also after `ClubAdmin` was there.
- Only `ClubUser` gets the lock screen. A kiosk PC has no shortcut in the common Startup folder (the installer
  removes an old one), and `Cms.Launcher.exe` ends at once for every other account when `kiosk.json` names
  `ClubUser`.
- The password of `ClubUser` is random (32 characters). The installer makes a new one on every run, never
  prints it and never writes it to a file. Windows keeps it for the automatic sign-in as the LSA secret
  `DefaultPassword`, not as the registry value `DefaultPassword` (local users can read that value).
- To reset the password of `ClubUser`, run the installer again with the same options. Nobody needs to know the
  password: staff do not sign in as `ClubUser`.
- The lock screen shell, "Task Manager off" and "no context menus in Start" apply to `ClubUser` only (its own
  registry hive). `ClubAdmin` and the other administrators keep `explorer.exe` and Task Manager. The installer
  removes the machine-wide values of older versions (shell, `DisableTaskMgr`, `DisableContextMenusInStart`).
- If the installer cannot load the registry hive of `ClubUser`, it stops with exit code 1 and changes no
  machine-wide setting. Sign out `ClubUser` or restart the PC, then run the installer again.
- `ClubAdmin` is the recovery account of the staff (Administrators). To work on a kiosk PC:
  1. Press Ctrl+Alt+Del on the PC, select **Switch user**, then sign in as `ClubAdmin`. The session of `ClubUser`
     stays (disconnected), with its lock screen. `ClubAdmin` gets `explorer.exe`, Task Manager and no lock screen.
     While `ClubAdmin` is signed in, the agent never restarts the PC.
     WARNING: Sign in within 120 s after "Switch user". On a locked PC that runs 5 minutes or more, the 120-s count
     starts at the sign-in screen (only the disconnected `ClubUser` is left). If the PC restarts, wait until
     `ClubUser` is back and do step 1 again.
  2. When you are done, sign out of `ClubAdmin` (Start > `ClubAdmin` > Sign out). The PC shows the Windows sign-in
     screen; about 2 minutes later the agent restarts the PC (the PC is locked and only the disconnected session of
     `ClubUser` is left), and `ClubUser` signs in at the start with the lock screen.

  A player can also select "Switch user" (Ctrl+Alt+Del cannot be blocked). Then the PC shows the Windows sign-in
  screen and nobody can sign in as `ClubUser` (nobody knows its password). On a locked PC the agent restarts it about
  2 minutes later. During a running session it does not (the paid time would end): end the session in the admin UI
  (the PC locks, and the restart follows 2 minutes later), or send "Log off" (the PC restarts at once). From another
  PC, an RMM tool or the guest agent runs as SYSTEM and needs no sign-in.
  When `ClubAdmin` does not exist, the installer asks for its password in a console. For a run without a console,
  give the password as a SecureString, for example from PowerShell:

  ```powershell
  .\install-client.ps1 -ServerUrl "http://10.3.3.1:5081" -EnrollmentToken "<token>" -KioskMode -AdminPassword (Read-Host -AsSecureString)
  ```

  Without `-AdminPassword` and without a console (SYSTEM), the installer creates no `ClubAdmin` and prints a
  warning. Then create a local administrator for the staff yourself.
- The shell of `ClubUser` is `Cms.Launcher.exe --watchdog`: a process without a window that starts the lock
  screen and starts it again within about a second when it ends (a player killed it, or it failed). The lock
  screen in turn starts a new watchdog when the watchdog is gone. The scheduled task `ClubLauncherWatchdog` of
  `ClubUser` (at sign-in and every minute) starts the watchdog when both were ended. The agent logs a warning when
  the lock screen does not answer while the PC must be locked, and the admin UI shows ⚠️ on the PC.
- While the PC is locked, the lock screen stays on top: every second it takes the foreground back and pushes down
  the top-most windows of other programs. A window in a band above the normal desktop windows (for example the
  "Open with" dialog of Windows 11, which no top-most window can cover) gets a close request at once, and its
  program is ended 2 s later when the window is still there. Programs of other Windows users, of other sessions and
  the Windows parts (shell, touch keyboard, accessibility tools, by full path) are never touched.
- `uninstall-kiosk.ps1` removes the kiosk shell, the policies of `ClubUser`, the automatic sign-in, its LSA secret
  and the task `ClubKioskAutoLogon`, and the scheduled task `ClubLauncherWatchdog`. When you answer `y`, it signs
  `ClubUser` out of every session (also a disconnected one after "Switch user") and waits up to 60 s until Windows
  has released the profile; then it deletes the account and `C:\Users\ClubUser` (also the read-only folders of the
  profile and the Windows files whose attributes an administrator may not change, for example
  `AppData\Local\Microsoft\Windows\SFAP\cache0.bin`; a junction inside is removed as a link, its target stays). An
  item that cannot be deleted does not stop the delete: the script lists the items that stay. It removes
  `kiosk.json` before it signs `ClubUser` out, so the agent does not restart the PC during the uninstall.
  Exit code 0 only when the account and the profile folder are both gone. Otherwise exit code 1, and the last lines
  say what stays and what to do: restart the PC and run the script again (a second run also deletes a profile
  folder that an earlier run left), or, when the answer was not `y`, run it again and answer `y`.

## Session teardown

At the end of a session (ended at the desk, time over, money used up, moved to another PC, or a staff unlock
without a session that is locked again) the lock screen of the PC:

1. closes the programs it started from the game grid, with their child processes, and the processes of the
   games it started;
2. closes every process with a name from `KillProcessNames` (by default the game launchers, Discord, Telegram
   and the browsers), only in the player's Windows session; Windows processes and the shell are never closed;
3. when `Cleanup.Enabled` is `true`, deletes the `Cleanup.Paths`, only inside `Cleanup.AllowedRoots`;
4. reports what it did to the agent; the audit log shows `device.session_teardown` ("3 program(s) closed
   (steam, cs2, Discord); cleanup removed 31 file(s) …").

A pause or a staff lock during the session closes nothing. The settings are in `C:\ClubAgent\appsettings.json`
on each PC (only administrators can change the folder; the installer keeps the section on a reinstall):

```json
"Teardown": {
  "KillProcessNames": [ "steam", "steamwebhelper", "EpicGamesLauncher", "Discord", "chrome", "msedge", "firefox" ],
  "Cleanup": {
    "Enabled": false,
    "AllowedRoots": [ "%LOCALAPPDATA%", "%APPDATA%" ],
    "Paths": [ "%LOCALAPPDATA%\\Google\\Chrome\\User Data", "%APPDATA%\\discord\\Local Storage" ]
  }
}
```

- `%VARIABLES%` are expanded in the player's Windows account (`%LOCALAPPDATA%` = the player's profile).
- Turn the cleanup on only for a shared kiosk account (`-KioskMode`: all players use `ClubUser`), so that
  the next player never gets the browser, chat or launcher login of the previous one. Check the paths first:
  everything under them is deleted (for example all Chrome profiles of the account).
- A path outside the allowed roots, a drive, an allowed root itself, a relative path or a path with an
  unknown variable is skipped. A symbolic link, junction or other reparse point is never followed: as the
  path, in the path or inside the deleted tree it is skipped, its target stays.
- Steam stores the saved logins in its install folder (`C:\Program Files (x86)\Steam\config\loginusers.vdf`).
  To delete it, add `C:\\Program Files (x86)\\Steam\\config` to `AllowedRoots` and the file to `Paths`.
- After a change, restart the agent (`Restart-Service ClubAgent`): it writes the settings to
  `C:\ProgramData\ClubAgent\teardown.json` for the lock screen.

## Acceptance Checks

- Admin UI opens at `http://10.3.3.1:5081` and asks for a login.
- A Cashier account cannot open Finance, change tariffs or delete anything.
- Client appears online and "approved" in the admin UI.
- A newly installed PC shows the locked lock screen (no session, no game grid) after the first start.
- A staff unlock of a PC without a session unlocks it; after `Restart-Service ClubAgent` the PC is locked again.
- Kiosk mode: `reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultPassword` finds
  no value; ClubUser signs in automatically after a restart; ClubAdmin (Ctrl+Alt+Del > Switch user) gets
  `explorer.exe` without the lock screen and can open Task Manager; about 2 minutes after ClubAdmin signs out the PC
  restarts and ClubUser signs in automatically again; after a staff "Log off" the PC restarts and ClubUser signs in.
- Ctrl+Shift+U on a locked PC asks for the staff PIN; the correct PIN unlocks it.
- Stopping the ClubAgent service (as admin) keeps the PC locked after 15 s; the staff PIN still unlocks it.
- Unlocking a PC without a session appears in the audit log as `device.unlock_without_session`.
- Lock and unlock commands work within a few seconds.
- A timed session unlocks the PC, counts down, and locks at expiry.
- Restarting the client during an active session preserves the original end time.
- Restarting the ClubAgent service does not require reinstalling the client.
- A session started at 16:30 and ended at 17:30 costs 30 minutes of the day price plus 30 minutes of the
  evening price (with such rules); the price quote in the start dialog shows the split.
- A night pack started at 23:00 ends at 08:00; a pause does not move that time.
- Pause locks the PC and stops the countdown; resume unlocks it with the end moved by the pause.
- Transfer to a free PC locks the old PC and unlocks the new one; a dearer zone shows the price difference.
- Players → card: the balance and the bonus match the ledger ("Баланс збігається з журналом").
  A Cashier can top up but cannot refund or correct.

## Network Security

- The club LAN uses plain HTTP. Staff passwords, login cookies and device keys cross the network
  unencrypted. Keep switch port isolation on, put the server and the admin PC in a network that players
  cannot reach, or use an HTTPS reverse proxy and set `Auth__CookieSecure=always`.
- Do not forward port 5081 from the internet to the server.
- Players must be standard Windows users on the club PCs (the kiosk install does this). A local
  administrator can stop the agent or read its key.

## Wake-on-LAN

The agent reports the MAC address of the network card that reaches the server with every heartbeat (admin UI,
Devices: column MAC). Owner, Manager or Tech can then start a PC from the PC map or the Devices tab (⚡); the
server sends three Wake-on-LAN "magic packets" (UDP) to `Power:WakeBroadcast`, port `Power:WakePort`.

- Enable Wake-on-LAN on every PC: in the BIOS / UEFI ("Wake on LAN", "Power on by PCI-E"), and in Windows
  (Device Manager → network card → Power Management: "Allow this device to wake the computer", "Only allow a
  magic packet"; Advanced: "Wake on Magic Packet"). Turn off "Fast startup" in the power options.
- Option A (Windows server in the club LAN): set `Power:WakeBroadcast` to the broadcast address of the PC
  network, for example `10.3.3.255` (installer folder `appsettings.json`, section `Power`), then restart the
  `ClubServer` service.
- Option B (Docker): a container on the compose network cannot broadcast into the club LAN. Either run the
  server with `network_mode: host` on a Linux host in the club LAN, or send the packets to a relay: on MikroTik
  for example a script with `/tool wol mac=<MAC> interface=<bridge>`, or a directed broadcast that the router
  forwards. Set `CMS_WAKE_BROADCAST` / `Power__WakeBroadcast` accordingly.
- With switch port isolation the broadcast must still reach the ports of the PCs (the isolation of most
  switches lets traffic from the uplink port through).
- Shutdown and reboot go through the command queue of the PC (`shutdown /s /t 5`, `shutdown /r /t 5` by the
  agent); a command for a PC that is off runs when it comes back within an hour, else it expires.
- A shutdown or reboot does not end a running session: it goes on and is billed. The confirmation dialog says so.

## Public booking page

The booking page `/book` lets guests send reservation requests from their phones. It is off until the Owner or a
Manager turns it on (admin UI → Бронювання / Reservations → settings). Inside the club LAN it works at once
(`http://<server>:5081/book`). To offer it on the internet, never forward port 5081; put an HTTPS reverse proxy in
front and publish **only** these paths:

- `/book` (the page), `/_next/` (its scripts, styles and fonts), `/favicon.ico`
- `/api/public/booking` (the three requests of the page)
- `/api/public/theme` and `/api/public/theme/logo` (the club theme and logo of the page; without them the page shows the
  Cyber theme without the logo)

Everything else (the admin UI, `/api/...` of the staff and of the PCs, `/swagger`) stays inside the club. The server
limits requests per client IP; behind a proxy set `ReverseProxy__TrustForwardedHeaders=true` and the right
`ReverseProxy__ForwardLimit`, so it sees the guests' addresses and not the proxy's. Example (nginx in front of the
club server):

```nginx
location = /book                 { proxy_pass http://club-server:5081; }
location /_next/                 { proxy_pass http://club-server:5081; }
location = /api/public/booking   { proxy_pass http://club-server:5081; proxy_set_header X-Forwarded-For $remote_addr; }
location /api/public/booking/    { proxy_pass http://club-server:5081; proxy_set_header X-Forwarded-For $remote_addr; }
location = /api/public/theme     { proxy_pass http://club-server:5081; proxy_set_header X-Forwarded-For $remote_addr; }
location = /api/public/theme/logo { proxy_pass http://club-server:5081; proxy_set_header X-Forwarded-For $remote_addr; }
location /                       { return 404; }
```

Requests wait in the Reservations tab ("Запити з сайту") with the name and the phone; staff call the guest and confirm
or decline. Names and phone numbers are erased automatically 30 days (setting) after the slot.

## Features That Need Extra Network Design

- UDP auto-discovery may fail because isolated switch ports block broadcast-style discovery.
- Wake-on-LAN from a Docker server needs host networking or a relay on the router (see [Wake-on-LAN](#wake-on-lan)).
- Direct PC-to-PC remote desktop or game hosting requires router forwarding, VPN, or a relay.
- Diskless boot/PXE is not part of this deployment path and needs a different network design.
