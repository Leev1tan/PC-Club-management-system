# PC Club Management System

[![CI](https://github.com/Leev1tan/PC-Club-management-system/actions/workflows/ci.yml/badge.svg)](https://github.com/Leev1tan/PC-Club-management-system/actions/workflows/ci.yml)
[![License: AGPL-3.0-or-later](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue.svg)](LICENSE)

PC Club Management System is an on-prem management platform for gaming clubs and cybersport venues. It provides central control of Windows gaming PCs, timed sessions, lock and unlock flows, device monitoring, and deployable server and client installers.

Project authorship: created and maintained by Volodymyr Shabat.

**English.** An open-source, self-hosted system to run a computer club: a live map of the PCs, sessions with time-of-day tariffs, packages and night packs, player balances with bonuses and an append-only money ledger, a Windows lock-screen shell (kiosk) and remote commands. The server runs inside the club (on a Windows PC or in Docker), so the club keeps working when the internet is down. The gaming PCs are Windows 10/11.

**Українською.** Відкрита система керування комп'ютерним клубом, яка працює локально у вашому клубі: карта ПК, сесії з тарифами за часом доби, пакети й нічні пакети, баланси гравців із бонусами та журналом грошових операцій, захищена оболонка блокування для Windows (kiosk) і віддалені команди на ПК. Сервер стоїть у клубі (на Windows-ПК або в Docker), тому клуб працює навіть без інтернету. Ігрові ПК — Windows 10/11.

## Contents

- [Screenshots](#screenshots)
- [How it works](#how-it-works)
- [Features: done and planned](#features-done-and-planned)
- [Run the server: two options](#run-the-server-two-options)
- [Install the club PCs (Windows)](#install-the-club-pcs-windows)
- [Demo mode](#demo-mode)
- [Security](#security)
- [Configuration](#configuration)
- [Development](#development)
- [Documentation](#documentation)
- [License](#license)

## Screenshots

**Live demo:** https://club-demo.k3s.ksm.in.ua (demo mode: 20 simulated PCs, data resets every 6 hours, nothing is real).
You can also run the same [demo mode](#demo-mode) locally: it needs only Docker.

![Dashboard (demo mode)](docs/screenshots/dashboard.png)

Planned images (in `docs/screenshots/`): dashboard, PC map, session dialog, players, tariffs, finance.

## How it works

```text
 Admin browser ──HTTP──►  Club server  (Windows PC with the installer, or Docker)
                          ├─ admin UI (Next.js, static files)
                          ├─ Cms.Server API (ASP.NET Core 8)  ── PostgreSQL or SQLite
                          ▲
                          │ HTTP polling (heartbeat, commands), port 5081
 Gaming PCs (Windows 10/11)
   ├─ Cms.Agent.Service  (Windows service: registers, heartbeats, runs lock/unlock/restart/...)
   └─ Cms.Launcher       (WPF full-screen lock screen / kiosk shell, session timer)
```

| Part | Runs on | Folder |
| --- | --- | --- |
| Server API | Windows (service) or Linux/Windows container | [`Cms.Server`](Cms.Server) |
| Admin UI | served by the server (Windows) or its own container | [`admin-ui`](admin-ui) |
| Agent | every gaming PC, Windows only | [`Cms.Agent.Service`](Cms.Agent.Service) |
| Lock screen / kiosk | every gaming PC, Windows only | [`Cms.Launcher`](Cms.Launcher) |
| Shared contracts | library | [`Cms.Shared`](Cms.Shared) |
| Installers | Windows | [`scripts/installer`](scripts/installer), [`tools/Installer.Bootstrapper`](tools/Installer.Bootstrapper) |

More detail: [docs/Architecture.md](docs/Architecture.md).

### Tariffs, packages and money in short

- **Tariff** = base price per hour for a zone + rules such as "Mon–Thu 08:00–17:00: 45 ₴/h" or "every day 22:00–08:00: 40 ₴/h" (club time). Time is billed per minute and split at every rule boundary: a session 16:30–17:30 pays 30 minutes of the day price and 30 minutes of the evening price. Post-paid time is rounded up to the tariff step (1, 5, 15 minutes …) and the tariff can have a minimum charge.
- **Package** = fixed price in one zone: a duration (3 h, 5 h), a time window with a cut-off (night pack 22:00–08:00 ends at 08:00, whenever it starts), or both (morning pack). Sold from the player balance or for cash/card at the desk.
- **Pause** stops the time and locks the PC; **resume** moves the end by the paused time (never past a package cut-off). **Transfer** moves a session to a free PC; a move to a dearer zone pays the price difference for the remaining time.
- **Player money**: real balance + bonus balance. Cashback tiers add bonus on top-ups; bonus pays only for PC time, first by default. Every movement is a ledger row; refunds and corrections need Manager or Owner and a reason.

## Features: done and planned

Status of each product area. "Partial" means a basic version works and more is planned.

| # | Area | Status | Works today | Planned |
| --- | --- | --- | --- | --- |
| 1 | Lock screen and kiosk shell (Windows) | Partial | Full-screen lock, keyboard blocking, session countdown, staff PIN unlock (set by the owner, checked offline), stays locked if the agent stops | Low-time warnings, clean teardown of game and browser sessions |
| 2 | Sessions and tariffs | Partial | Tariffs per zone with time-of-day and weekday rules, per-minute billing split at every rule boundary, rounding step and minimum charge; prepaid and open-ended sessions; fixed packages (3 h, 5 h, morning pack) and night packs with a cut-off, bound to a zone; pause / resume (time stops) and transfer to another PC with the price difference; auto-lock at expiry | Promo codes and happy hours, post-paid limits for trusted players |
| 3 | Player accounts and balances | Partial | Players with salted password or PIN; real balance and a separate bonus balance (PC time only); cashback tiers on top-ups; top-up (cash/card), refund and corrections with a reason; append-only ledger of every money movement, reconciled with the balances; no negative balances, no double-spend | Loyalty tiers, bonus expiry, player web portal |
| 4 | PC map and remote control | Partial | Live PC grid by zone, online/offline, lock/unlock/restart/log off/message, start/extend/end session | Drag-and-drop map, Wake-on-LAN and shutdown, hardware telemetry, process kill |
| 5 | Bar / POS and stock | Planned | – | Product catalog, sales (cash, card, balance), stock tracking |
| 6 | Staff roles, shifts, cash reports, audit log | Partial | Staff login with roles (Owner, Manager, Cashier, Tech), immutable audit log | Shift open/close, cash variance, Z-report |
| 7 | Seat booking | Planned | – | Reservations on the map, no-show auto-cancel, public booking page |
| 8 | Game launcher and account pool | Planned | – | Game grid in the launcher, club account pool |
| 9 | Tournaments | Planned | – | Batch unlock of a zone for a match |
| 10 | Analytics and reports | Partial | Revenue today/week/month, occupancy by zone | Owner dashboard, occupancy heatmap, CSV export |
| 11 | Deployment | Partial | Windows installers, Docker compose, demo mode, CI | Pre-built release binaries, multi-club (public live demo: done) |
| 12 | Languages and currency | Partial | Admin UI in Ukrainian (strings of the new screens in one file, `admin-ui/app/i18n.ts`), prices in ₴, club time zone setting | Ukrainian/English switch, currency setting |

Every admin page and API call needs a staff login with a role; club PCs authenticate with their own device key. Read [Security](#security) before you install the system in a club.

## Run the server: two options

The club PCs are always Windows. For the **server** you can choose:

| | **A. Windows PC in the club (installer)** | **B. Docker compose (Linux, or Windows with Docker)** |
| --- | --- | --- |
| Install | `ClubServerSetup.exe` (built by `scripts/installer/build-packages.ps1`) | `docker compose up -d` |
| Runs as | Windows service `ClubServer` | 3 containers: PostgreSQL, API, admin UI |
| Database | SQLite (default) or PostgreSQL | PostgreSQL 16 in a container (volume `ccms-pgdata`) |
| Club URL (UI + API) | `http://<server-ip>:5081` | `http://<server-ip>:5081` (same) |
| Client PCs find the server | LAN auto-discovery (UDP 5082) or `-ServerUrl` | `-ServerUrl` only (no UDP discovery from containers) |
| Update | build new packages and run the installer again | rebuild or pull images, `docker compose up -d` |

Both options serve the same API on the same URL, so the client install and the MikroTik rules in [docs/ClubDeployment.md](docs/ClubDeployment.md) are the same.

### A. Windows PC in the club (installer)

1. On a Windows build PC with the .NET 8 SDK and Node.js 22, build the packages:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\installer\build-packages.ps1
   ```
   Output: `publish\installers\ClubServerSetup.exe` and `ClubClientSetup.exe` (plus zip packages).
2. On the club server PC, run as Administrator:
   ```powershell
   ClubServerSetup.exe                     # SQLite database in C:\ProgramData\PCClub\cms.db
   # options: -HttpPort 5081 -DataPath C:\ProgramData\PCClub -EnrollmentToken <token>
   #          -PostgresConnectionString "Host=...;Database=cms;Username=...;Password=..."
   ```
   At the end the installer prints the **one-time setup code** and the **enrollment token** for the club PCs.
3. Check: `Invoke-RestMethod http://localhost:5081/health` returns `healthy`.
4. First start: open `http://<server-ip>:5081`, enter the setup code (also in `C:\ProgramData\PCClub\setup-code.txt`) and create the Owner account. Then set the staff PIN for the lock screens (Settings) and create accounts for the staff (Staff).

Full guide with network and router rules: [docs/ClubDeployment.md](docs/ClubDeployment.md).

### B. Docker compose (Linux, or Windows with Docker)

Needs Docker Engine with the compose plugin (Linux) or Docker Desktop (Windows).

```bash
git clone https://github.com/Leev1tan/PC-Club-management-system.git
cd PC-Club-management-system
cp .env.example .env          # set POSTGRES_PASSWORD (letters and digits) and CMS_ENROLLMENT_TOKEN
docker compose up -d          # first run builds the two images
docker compose exec server cat /app/data/setup-code.txt   # one-time code for the first Owner
```

First start: open `http://<server-ip>:5081`, enter the setup code and create the Owner account. Instead of the setup screen you can set `CMS_INITIAL_OWNER_USERNAME` and `CMS_INITIAL_OWNER_PASSWORD` in `.env` for the first start (remove the password afterwards). Then set the staff PIN for the lock screens (Settings).

- Admin UI and API: `http://<server-ip>:5081` (the admin-ui container proxies `/api` to the server).
- Health: `http://<server-ip>:5081/api/health`
- API docs: `http://<server-ip>:5081/swagger`
- Logs: `docker compose logs -f server` · Stop: `docker compose down` (data stays in the volume).

The database schema is created and upgraded automatically at start (EF Core migrations). Images: `ccms-server` (ASP.NET Core 8, non-root, port 8080) and `ccms-admin-ui` (static UI on unprivileged nginx, port 3000). CI publishes them to `ghcr.io/<owner>/ccms-server` and `ghcr.io/<owner>/ccms-admin-ui` for version tags `v*`.

## Install the club PCs (Windows)

Games run on the club PCs, and these are Windows 10/11 machines. Each PC gets the agent (a Windows service) and the lock-screen launcher (WPF). Run on each gaming PC as Administrator:

```powershell
ClubClientSetup.exe -ServerUrl "http://<server-ip-or-router>:5081" -EnrollmentToken "<token>"
```

With the enrollment token the PC is approved at once. Without it, the PC appears as "pending" in the admin UI (Devices) and does nothing until staff approve it. Add `-KioskMode` for the lock-screen shell. This works the same for both server options. See [docs/ClubDeployment.md](docs/ClubDeployment.md) for the club network (switch isolation, MikroTik NAT, acceptance checks).

## Demo mode

Demo mode shows the system without any Windows PCs. With `DEMO_MODE=true` the server:

- seeds a demo club with typical Ukrainian prices (₴): zones Standard (12 PCs), VIP (6) and Bootcamp/Pro (2); zone tariffs with weekday-day, weekend-day, evening and night prices; 3 h and 5 h packs, a morning pack and a night pack (22:00–08:00) per zone; cashback 5/10/15 % on top-ups; 8 players whose balances and bonuses come from their ledger history (top-ups, cashback, sessions, packages, a refund, a correction); about a week of session history and live sessions (packages, a paused session, an open-ended one);
- simulates the 20 PCs: heartbeats, and it runs queued commands (lock, unlock, session start/extend/end, restart, message, log off) like a real agent, so the PC map changes live; one PC stays offline on purpose; walk-in guests (cash or card) and players (from the balance) start sessions and buy packages now and then, and sessions are paused and resumed;
- wipes and re-seeds everything on start and every `DEMO_RESET_HOURS` hours (default 6);
- seeds four public demo staff accounts and shows them on the login page (see below);
- shows a "LIVE DEMO" banner in the admin UI, blocks `DELETE` requests, agent registration and changes of staff accounts, passwords and settings, and never connects to the internet.

Run it locally:

```bash
DEMO_MODE=true docker compose up -d      # then open http://localhost:5081
```

Safety: demo mode wipes its database. It refuses to start (exit code 1) on a database that contains data it did not create, so do not point it at a real club database; use a separate, empty one.

Logins (they exist **only in demo mode**; outside demo mode the server never creates an account by itself):

| User name | Password | Role |
| --- | --- | --- |
| `demo-owner` | `demo` | Owner |
| `demo-manager` | `demo` | Manager |
| `demo-cashier` | `demo` | Cashier |
| `demo-tech` | `demo` | Tech |

The login page shows these accounts as buttons. The demo player account **`demo` / `demo`** (for `POST /api/users/login`) exists only in the demo database; the other seeded players use the same password. All demo data is fictional.

Public demo behind an ingress (for example k3s): run one server replica, route `/` to `admin-ui:3000` and `/api` to `server:8080` (or route everything to `admin-ui:3000`), and add rate limiting at the ingress.

## Security

What protects a club installation (phase 2). Report security problems to the author (contact in [NOTICE.md](NOTICE.md)).

**Staff login and roles.** Every admin API call needs a staff login. The login is an HTTP-only, `SameSite=Strict` cookie (also `Secure` over HTTPS). Changing a password, a role or deactivating an account ends its logins at once. Passwords are hashed with ASP.NET Core Identity's PasswordHasher (salted PBKDF2). Five wrong passwords lock that user name for 15 minutes for that client IP; login is also rate limited per IP.

| Role | May do |
| --- | --- |
| Owner | everything, including staff accounts and settings (staff PIN) |
| Manager | sessions, players, refunds and balance/bonus corrections, tariffs and their rules, packages, cashback, zones, deletes, revenue, ledger, audit log |
| Cashier | sessions (start with a tariff or a package, pause, resume, transfer, extend, end), players, top-ups, PC commands; reads tariffs and packages; **not**: tariff/package/cashback changes, zones, deletes, refunds, corrections, revenue, ledger, audit log |
| Tech | PC map, devices (commands, zones, approval); nothing else |

**First start (no staff account yet).** The server never creates a default account. Either set `CMS_INITIAL_OWNER_USERNAME` and `CMS_INITIAL_OWNER_PASSWORD` (at least 8 characters) for the first start, or use the setup screen of the admin UI with the one-time code in `<data directory>/setup-code.txt`:

- Docker: `docker compose exec server cat /app/data/setup-code.txt`
- Windows installer: printed at the end of `ClubServerSetup.exe`, also in `C:\ProgramData\PCClub\setup-code.txt` (readable only by Administrators)

Both work only while no staff account exists; the code is deleted when the Owner is created.

**CSRF and CORS.** State-changing requests with the login cookie must send the header `X-CCMS-CSRF: 1` and a matching `Origin`; CORS allows the same origin only (`Cors__AllowedOrigins` for a UI on another origin). Scripts that use the API with a cookie must send the header too.

**Club PCs.** The server stores only a SHA-256 hash of each device key, and registration never returns the key of an existing PC. A new PC needs the enrollment token (`CMS_ENROLLMENT_TOKEN`, installer option `-EnrollmentToken`) or staff approval in the admin UI (Devices: approve, reject, revoke). The agent keeps its key and the token in `C:\ProgramData\ClubAgent\private` (no access for players) and deletes the token after the first registration; it sends the token only to an explicit `-ServerUrl`, never to a server found by LAN discovery.

**Lock screen.** There is no default staff PIN. The owner sets it in the admin UI (Settings, at least 8 characters); the server stores and sends only a PBKDF2 hash (600 000 iterations), and the launcher checks the PIN locally, so `Ctrl+Shift+U` + PIN also works while the server or the agent is down. The lock screen fails closed: when the agent heartbeat is older than 15 s or its state is missing, the PC stays locked. Three wrong PINs pause the prompt for 30 s, doubling up to 15 minutes.

**Money.** Every money movement is one row of an append-only ledger (`LedgerEntries`: top-up, refund, correction, session charge, package purchase, cashback, cashback reversal; walk-in guest payments at the desk too). A database trigger refuses `UPDATE` and `DELETE` on it, and a player with ledger rows cannot be deleted. Balance changes are single conditional database updates in the same transaction as the ledger row, so a balance (real or bonus) cannot go negative and cannot be spent twice; each row stores the balances after it, and `GET /api/ledger/reconcile` checks that every balance equals the sum of its rows. Bonus pays only for PC time and is never paid out; a refund takes back the cashback of the refunded amount. The database allows only one active session per PC. A post-paid session takes at most the balances; the rest is due at the desk (cash or card). On the first start of this version, the balances and the history of an earlier version are copied into the ledger once.

**Audit log.** Logins, failed logins, staff and settings changes, PC approval and commands, unlocks without an active session ("time without payment"), sessions (start, extend, pause, resume, transfer, end), top-ups, refunds, balance and bonus corrections, tariff, package and cashback changes are written to an append-only table (a database trigger refuses `UPDATE` and `DELETE`). Owner and Manager see it read-only in the admin UI (Audit log).

**Limits (read before you go live).**

- The club LAN uses plain HTTP on port 5081, so passwords, cookies and device keys cross the LAN unencrypted. Keep the server and the PCs in a network that players cannot sniff (switch port isolation, see [docs/ClubDeployment.md](docs/ClubDeployment.md)), or put an HTTPS reverse proxy in front and set `Auth__CookieSecure=always`. Never publish port 5081 to the internet without HTTPS.
- The staff PIN hash is readable on every club PC (the launcher needs it offline). Use a long PIN with letters and digits, and change it when staff leave.
- Players who are local administrators on a club PC can stop the agent or read its key; the kiosk install keeps players as standard users.
- The audit log is append-only in the application and the database, but a database administrator can still change it; it is not cryptographically chained.

## Configuration

Server settings (environment variables; in `appsettings.json` use `:` instead of `__`):

| Variable | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` | empty | PostgreSQL connection string. Empty, or one that contains `localhost`, selects SQLite. |
| `Database__Provider` | auto | `postgres` or `sqlite` to override the automatic choice (the installer sets it). |
| `Storage__DataDirectory` | Windows: `C:\ProgramData\PCClub`; else `<app>/data` (`/app/data` in the image) | Absolute data directory: SQLite file, login cookie keys, setup code. |
| `Database__SqlitePath` | `<data directory>/cms.db` | SQLite file; a relative path is resolved against the data directory. A `cms.db` of an earlier version (working directory, install folder or `C:\Windows\System32`) is copied there once. |
| `CMS_INITIAL_OWNER_USERNAME` / `CMS_INITIAL_OWNER_PASSWORD` | empty | First Owner account; used only while no staff account exists. |
| `CMS_ENROLLMENT_TOKEN` (or `Devices__EnrollmentToken`) | empty | Secret for the agent installer; PCs with it are approved at once. |
| `Devices__AllowPendingRegistration` | `true` | PCs without the token wait for approval; `false` refuses them. |
| `Auth__SessionHours` | `12` | Login lifetime (sliding). |
| `Auth__CookieSecure` | `auto` | `always` = login cookie only over HTTPS (behind an HTTPS proxy). |
| `Auth__LoginRateLimitPerMinute` | `20` (`300` in demo mode) | Login, setup and agent registration attempts per client IP. |
| `ReverseProxy__TrustForwardedHeaders` | `false` (`true` in the image) | Take the client IP and scheme from `X-Forwarded-*` (only when the server is reachable through the proxy only). |
| `ReverseProxy__ForwardLimit` | `1` | Number of proxies in front of the server (2 = ingress + admin-ui container). |
| `Retention__HeartbeatDays` / `Retention__HeartbeatSampleMinutes` | `7` / `5` | Keep heartbeat samples 7 days; store one sample per PC per 5 minutes. |
| `Retention__CommandDays` / `Retention__CommandExpireMinutes` | `30` / `60` | Delete finished commands after 30 days; a command not delivered in 60 minutes expires and never runs. |
| `Urls` / `ASPNETCORE_URLS` | `http://0.0.0.0:5081` (`http://+:8080` in the image) | Listen address. |
| `Club__TimeZone` | `Europe/Kyiv` | Club time zone for time-of-day tariff rules and package windows (IANA or Windows id). Docker: `CMS_TIMEZONE`; Windows installer: `-TimeZone`. |
| `DEMO_MODE` | `false` | Demo club and simulated PCs. |
| `DEMO_RESET_HOURS` | `6` | Demo reset interval; `0` = reset only on start. |
| `Cors__AllowedOrigins` | empty | Comma-separated origins, only if the UI is served from another origin (same origin only by default). |
| `Discovery__Enabled` | `true` (`false` in the image) | UDP LAN discovery on port 5082. |

Admin UI container: `API_UPSTREAM` (default `http://server:8080`) is where `/api`, `/swagger` and `/health` are proxied. The UI always calls the API on its own origin; the build argument `NEXT_PUBLIC_API_URL` can point it to another origin.

Docker compose (`.env`): `POSTGRES_PASSWORD`, `CMS_ENROLLMENT_TOKEN`, `CMS_INITIAL_OWNER_USERNAME` / `CMS_INITIAL_OWNER_PASSWORD`, `CMS_PORT` (5081), `CMS_BIND_ADDRESS` (0.0.0.0), `CMS_TIMEZONE` (Europe/Kyiv), `DEMO_MODE`, `DEMO_RESET_HOURS`, `CMS_SERVER_IMAGE`, `CMS_UI_IMAGE`. See [.env.example](.env.example). Server data (cookie keys, setup code) is in the volume `ccms-serverdata`, the database in `ccms-pgdata`.

## Development

| Task | Command |
| --- | --- |
| Run the API (SQLite) | `dotnet run --project Cms.Server` |
| Run the API in demo mode | `DEMO_MODE=true dotnet run --project Cms.Server` |
| Tests | `dotnet test tests/Cms.Server.Tests` (add `CMS_TEST_POSTGRES=<connection string>` to run the PostgreSQL tests too) |
| Admin UI dev server | `cd admin-ui && npm ci && NEXT_PUBLIC_API_URL=http://localhost:5081 npm run dev` (server with `Cors__AllowedOrigins=http://localhost:3000`) |
| Admin UI build (static, `admin-ui/out`) | `cd admin-ui && npm run build` (downloads fonts at build time) |
| New database migration (PostgreSQL) | `dotnet ef migrations add <Name> --project Cms.Server` |
| Agent and launcher | Windows with the .NET 8 SDK or Visual Studio 2022 |

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) builds and tests the server on Ubuntu, builds the agent and the launcher on Windows, builds the admin UI and both Docker images.

## Documentation

- [Architecture](docs/Architecture.md)
- [Project Plan](docs/ProjectPlan.md)
- [Club Deployment Guide](docs/ClubDeployment.md)
- [Test Checklist](docs/TestChecklist.md)

## License

Copyright (C) Volodymyr Shabat. See [NOTICE.md](NOTICE.md).

This program is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version (**AGPL-3.0-or-later**). See [LICENSE](LICENSE).

If you run a modified version for other people over a network, the AGPL requires you to offer them its source code. A commercial license without these obligations can be arranged with the author (contact in [NOTICE.md](NOTICE.md)).
