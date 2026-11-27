# PC Club Management System — Architecture

Project authorship: created and maintained by Volodymyr Shabat.

## Overview

The PC Club Management System is a distributed application for managing gaming PCs in a cybersport club environment. It enables central control of Windows gaming stations including session management with time limits, device locking/unlocking, and remote commands.

```mermaid
graph TB
    subgraph Admin["Admin Layer"]
        AdminUI["Admin UI<br/>(Next.js)"]
    end

    subgraph Server["Server Layer"]
        API["Cms.Server<br/>(ASP.NET Core)"]
        DB[(PostgreSQL<br/>Database)]
    end

    subgraph Client["Client Machines"]
        Agent["Cms.Agent.Service<br/>(Windows Service)"]
        Launcher["Cms.Launcher<br/>(WPF Kiosk)"]
        State["state.json<br/>(Local File)"]
    end

    AdminUI -->|REST API| API
    API -->|EF Core| DB
    Agent -->|HTTP Polling| API
    Agent -->|Writes| State
    Launcher -->|Reads| State
```

---

## Solution Structure

| Project | Type | Description |
|---------|------|-------------|
| [Cms.Server](../Cms.Server) | ASP.NET Core Web API | Central management server with REST endpoints |
| [Cms.Agent.Service](../Cms.Agent.Service) | .NET Worker Service | Windows background service running on each PC |
| [Cms.Launcher](../Cms.Launcher) | WPF Application | Kiosk UI with lock screen overlay |
| [Cms.Shared](../Cms.Shared) | Class Library | Shared contracts (currently minimal) |
| [admin-ui](../admin-ui) | Next.js | Web dashboard for administrators |

---

## Component Details

### 1. Cms.Server (Central API)

**Technology**: ASP.NET Core 8, Entity Framework Core, PostgreSQL

**Entry Point**: [Program.cs](../Cms.Server/Program.cs)

**Key Components**:

| File | Purpose |
|------|---------|
| [DevicesController.cs](../Cms.Server/Controllers/DevicesController.cs) | Device registration, heartbeats, command queue |
| [SessionsController.cs](../Cms.Server/Controllers/SessionsController.cs) | Sessions: start (tariff or package), end, extend, pause, resume, transfer |
| [Billing/SessionService.cs](../Cms.Server/Billing/SessionService.cs) | Session rules and money handling (used by the API and the demo simulator) |
| [Billing/TariffCalculator.cs](../Cms.Server/Billing/TariffCalculator.cs) | Prices of PC time per time-of-day rule, rounding, minimum charge |
| [Billing/PackageWindows.cs](../Cms.Server/Billing/PackageWindows.cs) | When a package is on sale and when it ends (cut-off) |
| [Billing/Wallet.cs](../Cms.Server/Billing/Wallet.cs) | Atomic balance and bonus changes, append-only ledger, reconciliation |
| [CmsDbContext.cs](../Cms.Server/Data/CmsDbContext.cs) | EF Core DbContext with entity definitions |
| [IDeviceRepository.cs](../Cms.Server/Repositories/IDeviceRepository.cs) | Device operations interface |
| [ISessionRepository.cs](../Cms.Server/Repositories/ISessionRepository.cs) | Session operations interface |

**REST API Endpoints** (who may call them in brackets; see the README security section):

```
POST /api/auth/login | logout | setup | password, GET /api/auth/me | status   → staff login (cookie)
GET/POST/PUT /api/staff                     → staff accounts                  [Owner]
GET/PUT/DELETE /api/settings[/staff-pin]    → lock-screen staff PIN           [Owner]
GET  /api/audit                             → audit log, read-only            [Owner, Manager]

POST /api/devices/register       → Register a PC: enrollment token, current key, or "pending"   [agent]
POST /api/devices/heartbeat      → Agent heartbeat; returns status + staff PIN hash             [X-Device-Key]
GET  /api/devices/{id}/commands  → Poll pending commands                          [X-Device-Key, approved]
POST /api/devices/{id}/commands/{cmdId}/ack → Acknowledge command                 [X-Device-Key, approved]
GET  /api/devices                → List all devices                               [all staff]
POST /api/devices/{id}/commands  → Enqueue lock/unlock/restart/logoff/message     [all staff]
POST /api/devices/{id}/approve | reject | revoke, PUT .../zone                    [Owner, Manager, Tech]

POST /api/sessions               → Start: tariff (prepaid minutes or open-ended) or packageId;   [Owner, Manager, Cashier]
                                   paymentMethod balance | cash | card, useBonus
POST /api/sessions/{id}/end      → End; open-ended: billed per segment, balances first, rest at the desk
POST /api/sessions/{id}/extend   → Add minutes to a prepaid or package session (never past a cut-off)
POST /api/sessions/{id}/pause | resume            → Stop / restart the time (PC locks / unlocks)
GET  /api/sessions/{id}/transfer-quote?targetDeviceId= , POST /api/sessions/{id}/transfer → move to a free PC
GET  /api/sessions               → List all sessions (active open-ended: currentCost)
GET  /api/sessions/revenue       → Revenue                                        [Owner, Manager]
GET  /api/tariffs, POST /api/tariffs/quote        → Tariffs with rules, price of prepaid minutes   [front desk]
POST/PUT/DELETE /api/tariffs     → Tariff with its rule list                      [Owner, Manager]
GET  /api/packages[?zoneId|deviceId]              → Package catalog, "on sale now" and end time    [front desk]
POST/PUT/DELETE /api/packages    → Package catalog                                [Owner, Manager]
GET  /api/billing/settings, PUT  → Club time zone, cashback tiers (PUT: Owner, Manager)
POST /api/users/{id}/topup       → Top-up in cash or by card, cashback            [front desk]
POST /api/users/{id}/refund | adjust              → Refund / correction of balance or bonus, reason  [Owner, Manager]
GET  /api/users/{id}/ledger[/check]               → Player ledger, reconciliation with the balances  [front desk]
GET  /api/ledger, /api/ledger/reconcile           → Club ledger (read-only), reconciliation of all players  [Owner, Manager]
POST /api/users/login            → Player login on the lock screen (password or PIN required)   [public, rate limited]

GET  /health, /api/health        → Health check                                   [public]
GET  /api/demo                   → Demo mode status (banner in the admin UI)      [public]
```

---

### 2. Cms.Agent.Service (Windows Service)

**Technology**: .NET 8 Worker Service, runs as Windows Service

**Entry Point**: [Worker.cs](../Cms.Agent.Service/Worker.cs)

**Responsibilities**:
1. **Registration**: with the enrollment token from the installer (approved at once) or without it
   (the PC waits for staff approval); stores `deviceId` and `deviceKey` in `ClubAgent\private`
2. **Heartbeat Loop**: Sends a heartbeat every 5 seconds; receives its status and the staff PIN hash
3. **Command Polling**: Fetches and executes pending commands from server
4. **Session Enforcement**: Tracks session end time, manages lock state
5. **State File Management**: Writes `state.json` for Launcher to read

**Supported Commands**:

| Command | Action |
|---------|--------|
| `lock` | Lock the PC (write lock state to file) |
| `unlock` | Unlock the PC |
| `restart` | Schedule system restart in 5 seconds |
| `message` | Log message (UI popup planned) |
| `session_set` | Set the session end time; `{"endUtc": null}` = no end (open-ended or paused session) |

**Local State Communication** (written every second, also while the server is unreachable):
```
%ProgramData%\ClubAgent\          # players: read only (installer ACL)
├── state.json           # Lock state, remaining seconds, time of the last lock change
├── agent_heartbeat.txt  # Agent liveness marker (the launcher locks when it is older than 15 s)
├── staff-pin.json       # PBKDF2 hash of the staff PIN (from the server)
└── private\             # SYSTEM, Administrators, agent only
    ├── device.json            # device id + key
    └── enrollment-token.txt   # from the installer; deleted after the first registration
```

---

### 3. Cms.Launcher (Kiosk UI)

**Technology**: WPF (.NET 8), runs in full-screen kiosk mode

**Entry Point**: [MainWindow.xaml.cs](../Cms.Launcher/MainWindow.xaml.cs)

**Features**:
- Full-screen overlay when PC is locked
- Session countdown timer display
- Keyboard blocking when locked (via [KeyboardBlocker.cs](../Cms.Launcher/KeyboardBlocker.cs))
- Staff unlock with the staff PIN (`Ctrl+Shift+U`); the owner sets the PIN in the admin UI, the
  launcher verifies it locally against `staff-pin.json`, so it also works offline. No default PIN.
- Fail closed: stays locked when the agent heartbeat is older than 15 s or `state.json` is missing

**State Polling**:
- Reads `state.json` every 2 seconds
- Shows/hides lock overlay based on `isLocked` field
- Displays remaining time from `remainingSeconds` field

---

### 4. Admin UI (Next.js)

**Technology**: Next.js 15 (static export), React 19, Tailwind CSS

**Entry Point**: [page.tsx](../admin-ui/app/page.tsx)

**Features**:
- Real-time device list (polls every 5 seconds)
- Device status indicators (online/offline)
- Quick action buttons: Lock, Unlock, Restart, Start Session
- Session duration prompt

---

## Data Model

```mermaid
erDiagram
    DeviceEntity {
        Guid Id PK
        string Hostname
        string OsVersion
        string AgentVersion
        string DeviceKey UK
        DateTimeOffset LastSeenUtc
        string LastIp
    }

    SessionEntity {
        Guid Id PK
        Guid DeviceId FK
        DateTimeOffset StartUtc
        DateTimeOffset EndUtc
    }

    HeartbeatEntity {
        Guid Id PK
        Guid DeviceId FK
        DateTimeOffset CreatedUtc
        double CpuPercent
        double MemPercent
        string ActiveUser
        string Ip
    }

    DeviceEntity ||--o{ SessionEntity : "has"
    DeviceEntity ||--o{ HeartbeatEntity : "has"
```

---

## Billing (sessions, tariffs, packages, money)

```mermaid
erDiagram
    TariffEntity ||--o{ TariffRuleEntity : "time-of-day rules"
    ZoneEntity ||--o{ TariffEntity : "zone tariffs"
    ZoneEntity ||--o{ PackageEntity : "packages"
    SessionEntity ||--o{ SessionSegmentEntity : "play time per PC / tariff"
    UserEntity ||--o{ LedgerEntryEntity : "money movements (append-only)"
    SessionEntity }o--|| PackageEntity : "bought package"
```

- **Club time.** Rules and package windows use the club time zone (`Club:TimeZone`, default
  Europe/Kyiv); the server may run in UTC. Prices are looked up per club-local minute, so rule
  boundaries, midnight and DST changes split a session into slices with their own price.
- **Rules.** A tariff has a base price and an ordered rule list (days of the week the window starts
  on, start, end — end ≤ start runs past midnight, price). The first matching rule wins.
- **Prepaid vs. post-paid.** Prepaid minutes are priced when the session starts (exact minutes, the
  minimum charge applies). Open-ended time is priced at the end from its segments: each segment with
  its tariff and rules; the total time is rounded up to the tariff step (the first 10 s of a started
  step are free); the minimum charge applies when anything was played.
- **Segments.** Start opens a segment; pause closes it; resume opens a new one; a transfer closes the
  segment on the old PC and opens one on the new PC with the new zone's tariff.
- **Packages.** Fixed price per zone: duration, time window with a cut-off, or both. A session with a
  cut-off never runs past it (pause, transfer, extend). A move to another zone uses the package of the
  same kind there and pays the pro-rated price difference; a move to a cheaper zone refunds nothing.
- **Ledger.** `LedgerEntries` is append-only (trigger + `SaveChanges` guard). Columns: balance delta,
  bonus delta, desk amount (cash/card in or out), balances after the row, method, session, package,
  reason, staff member. Players' `Balance` / `BonusPoints` are cached sums changed only together with a
  row (single conditional `UPDATE`, never below zero). `GET /api/ledger/reconcile` and the player check
  compare them with the sums and the chain of "balance after" values. The table `Transactions` of
  earlier versions is copied once (`LedgerBackfill`) and not written any more.
- **Bonus.** Cashback tiers on top-ups (`billing.bonus_topup_rules`). Bonus pays only for PC time
  (sessions, packages, extensions, transfer differences), bonus first unless `useBonus = false`; it is
  never refunded; a refund takes back the cashback of the refunded amount.

## Communication Flow

### Device Registration
```mermaid
sequenceDiagram
    participant Agent as Agent Service
    participant Server as Cms.Server
    participant DB as PostgreSQL

    Agent->>Server: POST /api/devices/register<br/>{hostname, os, version}
    Server->>DB: Create DeviceEntity
    Server-->>Agent: {deviceId, deviceKey}
    Note over Agent: Store deviceKey for auth
```

### Session Lifecycle
```mermaid
sequenceDiagram
    participant Admin as Admin UI
    participant Server as Cms.Server
    participant Agent as Agent Service
    participant Launcher as Launcher

    Admin->>Server: POST /api/sessions<br/>{deviceId, durationMinutes}
    Server->>Server: Create Session
    Server->>Server: Enqueue session_set + unlock
    Server-->>Admin: SessionView

    loop Every 1s
        Agent->>Server: GET /api/devices/{id}/commands
        Server-->>Agent: [{type: session_set, endUtc}]
        Agent->>Agent: Set _sessionEndUtc
        Agent->>Agent: Write state.json
    end

    loop Every 2s
        Launcher->>Launcher: Read state.json
        Launcher->>Launcher: Update countdown
    end

    Note over Agent,Launcher: When time expires
    Agent->>Agent: Set isLocked=true
    Launcher->>Launcher: Show lock overlay
```

---

## Deployment Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Admin Workstation                        │
│  ┌─────────────────┐                                        │
│  │   Admin UI      │◄────────────── HTTP :3000              │
│  │   (Next.js)     │                                        │
│  └────────┬────────┘                                        │
│           │                                                  │
└───────────┼──────────────────────────────────────────────────┘
            │ REST API
            ▼
┌─────────────────────────────────────────────────────────────┐
│                      Server (On-Prem)                        │
│  ┌─────────────────┐     ┌─────────────────┐                │
│  │   Cms.Server    │────►│   PostgreSQL    │                │
│  │   (Kestrel)     │     │   :5432         │                │
│  │   :5081         │     └─────────────────┘                │
│  └─────────────────┘                                        │
└───────────┬──────────────────────────────────────────────────┘
            │ HTTP Polling
            ▼
┌─────────────────────────────────────────────────────────────┐
│                    Gaming PC (Windows 11)                    │
│  ┌─────────────────┐     ┌─────────────────┐                │
│  │ Cms.Agent       │────►│ state.json      │                │
│  │ (Windows Svc)   │     └────────┬────────┘                │
│  └─────────────────┘              │                         │
│                                   ▼                         │
│                      ┌─────────────────┐                    │
│                      │  Cms.Launcher   │                    │
│                      │  (WPF Kiosk)    │                    │
│                      └─────────────────┘                    │
└─────────────────────────────────────────────────────────────┘
```

---

### Server deployment options

The club PCs always run Windows (agent + launcher). The server has two equal options:

| | Windows PC (installer) | Docker compose |
|---|---|---|
| Server process | `ClubServer` Windows service (`Cms.Server.exe`) | `ccms-server` container (Linux, ASP.NET Core 8) |
| Admin UI | static files in the server `wwwroot` | `ccms-admin-ui` container (nginx), proxies `/api` to the server |
| Database | SQLite (default) or PostgreSQL | PostgreSQL 16 container |
| Club URL | `http://<server>:5081` | `http://<server>:5081` |
| Schema | SQLite: created on start; PostgreSQL: EF Core migrations on start | EF Core migrations on start |

In both cases the admin UI calls the API on its own origin (`/api/...`). For a public demo behind an
ingress, route `/` to the admin UI and `/api` to the server. See the [README](../README.md) for
commands and [ClubDeployment.md](ClubDeployment.md) for the club network.

---

## Security Considerations

| Aspect | Implementation |
|--------|----------------|
| **Staff login** | Cookie (HTTP-only, SameSite=Strict), roles Owner / Manager / Cashier / Tech, CSRF header + Origin check |
| **Passwords** | ASP.NET Core Identity PasswordHasher (salted PBKDF2) for staff and players |
| **Agent Authentication** | `X-Device-Key` header; the server stores only its SHA-256 hash |
| **PC enrollment** | Enrollment token from the installer, or staff approval of a pending PC |
| **CORS** | Same origin only; `Cors:AllowedOrigins` for exceptions |
| **Staff Unlock** | Staff PIN set by the owner, PBKDF2 hash verified locally by the launcher |
| **Fail-Closed** | Launcher stays locked when the agent is silent (>15 s) |
| **Audit log** | Append-only table (database trigger), read-only page for Owner and Manager |
| **Money ledger** | Append-only table (database trigger); balances change only with a ledger row; reconciliation endpoint |

---

## Technology Stack

| Layer | Technology |
|-------|------------|
| **Server** | .NET 8, ASP.NET Core, EF Core, PostgreSQL |
| **Agent** | .NET 8 Worker Service, Windows Service |
| **Launcher** | .NET 8, WPF, Windows Low-Level Keyboard Hook |
| **Admin UI** | Next.js 15 (static export), React 19, Tailwind CSS |
| **Build** | Visual Studio 2022, dotnet CLI, npm |

---

## Related Documentation

- [ProjectPlan.md](ProjectPlan.md) — Strategy, milestones, and detailed planning
- [TestChecklist.md](TestChecklist.md) — Manual testing procedures
