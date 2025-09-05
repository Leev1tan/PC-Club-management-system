# Club Management System – Strategy and Tactics

This document is the end-to-end plan and execution guide for a cybersport PC club management system. It covers goals, architecture, APIs, data model, milestones, security, deployment, discovery, operations, and acceptance criteria.

## 1) Goals and Scope

- Business goals
  - Central control of Windows 11 gaming PCs (lock/unlock, sessions, remote commands)
  - Timed sessions for members/guests; basic reporting
  - Local-first reliability (works on LAN if internet drops)
- Out of scope for MVP
  - Full payment processing, loyalty, online bookings
  - Deep launcher integrations beyond basic scripting

## 2) Strategy

- Build a Windows agent (service + optional kiosk UI) and a central server API; share contracts via a library
- Implement the control loop first: register -> heartbeat -> commands -> sessions
- Ship on-prem server first; add cloud sync later if needed
- Use polling for MVP; add SignalR real-time after

## 3) Architecture

- Agent Service (Windows .NET 8)
  - Runs at boot, enforces lock/unlock, executes commands, sends heartbeats
  - Stores deviceId/secret after first registration
- Launcher (WPF)
  - Full-screen kiosk UI; talks to agent locally (named pipes or HTTP)
  - Shows remaining time, login/guest (post-MVP minimal)
- Server API (ASP.NET Core)
  - Devices, sessions, commands, reports; Swagger exposed
  - CORS open for dev; restrict later
- Database
  - PostgreSQL via EF Core (initially optional; in-memory for day 1)
- Admin UI
  - Swagger/Postman initially; simple web admin after MVP

## 4) Control Flows (MVP)

1. Registration
   - Agent POST /api/agents/register { hostname, os, version, token? }
   - Server returns { deviceId, deviceSecret }
2. Heartbeat (every 5s)
   - Agent POST /api/agents/heartbeat with X-Device-Key header
   - Payload: cpu%, mem%, active user, ip(s), uptime
3. Command fetch/ack
   - Agent GET /api/agents/{deviceId}/commands?max=10 (auth: X-Device-Key)
   - Agent executes; POST /api/agents/{deviceId}/commands/{id}/ack { status, result }
4. Sessions
   - Admin creates a session; agent enforces lock/unlock and time remaining
   - Launcher shows countdown; blocks when time is up

## 5) Data Model (initial)

- devices(id, name, hostname, os_version, agent_version, last_seen_utc, last_ip, status, secret_hash)
- device_heartbeats(id, device_id, created_utc, cpu_pct, mem_pct, active_user, ip, uptime)
- commands(id, device_id, created_utc, type, payload_json, status, result)
- sessions(id, device_id, user_id?, state, start_utc, end_utc, remaining_seconds)
- users(id, username, role, display_name)

## 6) Public API (MVP)

- POST /api/agents/register -> { deviceId, secret }
- POST /api/agents/heartbeat -> 200
- GET  /api/agents/{deviceId}/commands -> [Command]
- POST /api/agents/{deviceId}/commands/{id}/ack -> 200
- GET  /api/devices -> [Device]
- POST /api/devices/{id}/commands { type, payload }
- GET  /api/sessions -> [Session]
- POST /api/sessions { deviceId, durationMinutes, userId? }
- POST /api/sessions/{id}/{pause|resume|end}

Auth (MVP): header X-Device-Key for agent endpoints; admin endpoints open on LAN during dev; lock down later.

## 7) Agent Command Set (v1)

- lock: show blocking overlay, stop disallowed apps
- unlock: remove overlay, allow launcher/games
- logoff: log off current user
- restart: reboot device
- message: show modal notification

## 8) Device Discovery

- Recommended: self-registration by agent using provisioning URL/token (no scanning required)
- Optional discovery modes (post-MVP):
  - Subnet ping/TCP sweep for known port (simple, may be noisy)
  - Agent multicast beacons on LAN; server listens (fast, may be filtered across VLANs)
  - DHCP leases or SNMP tables (requires access to router/switch)
- Note: devices behind a switch and router can still self-register to the server IP; cross-subnet scanning has limits

## 9) Security

- Per-device shared secret; store hashed server-side
- Run service as LocalService; least privilege
- Validate input, rate-limit, audit logs
- TLS for server when feasible; code-sign agent for deployment

## 10) Deployment

- Server: Docker compose (API + Postgres); dev can run Kestrel directly
- Agent: self-contained publish + scripts for install; MSI later
- Launcher: deployed with agent; autostart for kiosk user

## 11) Observability

- Agent logs: Windows Event Log
- Server logs: console/file; add Serilog later
- Health endpoints: /health on server

## 12) Milestones and Estimates

- M1 (1–2 days): register + heartbeat + devices list
- M2 (1–2 days): commands (lock/unlock/logoff/restart/message)
- M3 (1 day): sessions + launcher time display
- M4 (0.5–1 day): Postgres + EF Core persistence
- M5 (0.5–1 day): packaging/deployment polish
- Optional M6 (1–2 days): simple admin UI

## 13) Tactics (execution order)

1) Server: DevicesController (register, heartbeat, list)
2) Agent: persist keys; loop register/heartbeat/commands; implement handlers
3) Launcher: full-screen shell; show time; basic login/guest later
4) Admin: use Swagger; add small web page later
5) Security hardening; packaging

## 14) Test Plan

- Unit: command serialization, session timing
- Integration: register/heartbeat; command round-trip
- E2E: start server + agent; send lock/unlock
- Network: NAT/bridged, firewall on/off, multi-subnet

## 15) Risks

- Multicast filters (affect discovery) -> rely on self-registration
- Privilege needs for some actions -> selectively elevate, document
- Update/rollback -> plan atomic updates post-MVP

## 16) Acceptance Criteria (MVP)

- Agent appears in /api/devices within 10s of start
- Heartbeats every 5s; offline detection under 20s after stop
- Admin can send lock/unlock; agent enforces within 5s
- Session starts; launcher shows countdown and blocks at time up
- Restart safe; no manual intervention required

## 17) Roadmap Beyond MVP

- SignalR real-time; multi-venue; POS/payments; game platform automation; auto-update; cloud dashboard; policy controls
