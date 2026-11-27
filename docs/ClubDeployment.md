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

## Server Install

### Option A: Windows PC (installer)

Run on the ClubServer PC as Administrator:

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

Run on each gaming PC as Administrator:

```powershell
ClubClientSetup.exe -ServerUrl "http://10.3.3.1:5081" -EnrollmentToken "<token>" -KioskMode
```

- With the enrollment token the PC is approved at once. The agent deletes the token after the first
  registration. The token is sent only to the explicit `-ServerUrl`, never to a server found by discovery.
- Without the token the PC appears as "pending" in the admin UI (Devices). Approve it there (Owner,
  Manager or Tech). Reject unknown PCs.
- A reinstalled PC with the same name keeps its record (zone, history) when it registers with the token;
  its old key stops working. Revoke a PC that leaves the club (Devices, revoke button).

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

## Acceptance Checks

- Admin UI opens at `http://10.3.3.1:5081` and asks for a login.
- A Cashier account cannot open Finance, change tariffs or delete anything.
- Client appears online and "approved" in the admin UI.
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

## Features That Need Extra Network Design

- UDP auto-discovery may fail because isolated switch ports block broadcast-style discovery.
- Wake-on-LAN may require MikroTik-specific WOL or broadcast relay setup.
- Direct PC-to-PC remote desktop or game hosting requires router forwarding, VPN, or a relay.
- Diskless boot/PXE is not part of this deployment path and needs a different network design.
