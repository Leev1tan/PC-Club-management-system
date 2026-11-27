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
   when no session runs. The client installer prepares first (new files in `C:\ClubAgent.new` and
   `C:\ClubLauncher.new`, a backup in `C:\ClubAgent.old` and `C:\ClubLauncher.old`), then stops the agent
   and the lock screen (also its watchdog), copies and starts the agent. If the copy or the start fails, it
   restores the old version, starts it again and ends with exit code 1. On success it signs out ClubUser,
   and the automatic sign-in starts the new lock screen. The last line says `RESULT: OK` or
   `RESULT: FAILED`. The installer keeps the `Teardown` and `AccountPool` settings of
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

- With the enrollment token the PC is approved at once. The agent deletes the token after the first
  registration. The token is sent only to the explicit `-ServerUrl`, never to a server found by discovery.
- Without the token the PC appears as "pending" in the admin UI (Devices). Approve it there (Owner,
  Manager or Tech). Reject unknown PCs.
- A reinstalled PC with the same name keeps its record (zone, history) when it registers with the token;
  its old key stops working. Revoke a PC that leaves the club (Devices, revoke button).
- A new PC is locked. A club PC is locked unless a session runs on it or staff unlocked it in the admin UI.
  A staff unlock of a PC without a session ends when the agent or the PC restarts.
- The installer works as Administrator and as SYSTEM (RMM tools, Intune, a VM guest agent). It asks no
  question when it runs without a console.

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

### Kiosk mode (`-KioskMode`)

- The players use the Windows account `ClubUser`. It signs in automatically, and the lock screen is its shell.
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
- `ClubAdmin` is the recovery account of the staff (Administrators): Ctrl+Alt+Del > Switch user > `ClubAdmin`.
  When it does not exist, the installer asks for its password in a console. For a run without a console, give
  the password as a SecureString, for example from PowerShell:

  ```powershell
  .\install-client.ps1 -ServerUrl "http://10.3.3.1:5081" -EnrollmentToken "<token>" -KioskMode -AdminPassword (Read-Host -AsSecureString)
  ```

  Without `-AdminPassword` and without a console (SYSTEM), the installer creates no `ClubAdmin` and prints a
  warning. Then create a local administrator for the staff yourself.
- `uninstall-kiosk.ps1` removes the kiosk shell, the policies of `ClubUser`, the automatic sign-in and its LSA
  secret.

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
  no value; ClubUser signs in automatically after a restart; ClubAdmin gets `explorer.exe` and can open Task Manager.
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
