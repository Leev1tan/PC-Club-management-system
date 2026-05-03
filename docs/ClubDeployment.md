# Club Deployment Guide

Project authorship: created and maintained by Volodymyr Shabat.

This guide is for a club network where gaming PCs are on `10.3.3.100-255`, the MikroTik router is `10.3.3.1`, and switch ports isolate PCs from each other.

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

Run on the ClubServer PC as Administrator:

```powershell
ClubServerSetup.exe
```

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
ClubClientSetup.exe -ServerUrl "http://10.3.3.1:5081"
```

Check:

```powershell
Get-Service ClubAgent
Get-Content C:\ProgramData\ClubAgent\config.json
Get-Content C:\ProgramData\ClubAgent\state.json
```

Expected:

```text
ClubAgent is Running
config.json contains http://10.3.3.1:5081
state.json is created after the agent starts
```

## Acceptance Checks

- Admin UI opens at `http://10.3.3.1:5081`.
- Client appears online in the admin UI.
- Lock and unlock commands work within a few seconds.
- A timed session unlocks the PC, counts down, and locks at expiry.
- Restarting the client during an active session preserves the original end time.
- Restarting the ClubAgent service does not require reinstalling the client.

## Features That Need Extra Network Design

- UDP auto-discovery may fail because isolated switch ports block broadcast-style discovery.
- Wake-on-LAN may require MikroTik-specific WOL or broadcast relay setup.
- Direct PC-to-PC remote desktop or game hosting requires router forwarding, VPN, or a relay.
- Diskless boot/PXE is not part of this deployment path and needs a different network design.
