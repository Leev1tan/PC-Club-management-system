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
First set the staff PIN in the admin UI (Owner: Settings), wait 5 s for the next heartbeat, and check
that `C:\ProgramData\ClubAgent\staff-pin.json` exists. Lock the VM via admin UI, then on VM:
- [ ] Press Ctrl+Shift+U
- [ ] A masked PIN prompt appears; the lock screen stays visible behind it
- [ ] A wrong PIN is refused; after 3 wrong PINs the prompt waits 30 s
- [ ] The correct PIN drops the overlay
- [ ] A new lock from the admin UI locks the VM again

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

### Test 7: Fail-Closed (Service Down)
Start a session so that the VM is unlocked. On VM (as admin), stop the service:
```powershell
sc.exe stop ClubAgent
```
Wait 15+ seconds:
- [ ] Launcher overlay appears with "AGENT NOT RESPONDING" (fail closed)
- [ ] Keys stay blocked
- [ ] Ctrl+Shift+U + the staff PIN unlocks the VM (offline check)

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

### Test 9: Tariff Rules, Packages, Pause and Transfer (phase 3)
Prepare (Owner or Manager, admin UI → Тарифи): a tariff for the PC's zone with a rule ending 5 minutes
from now, a 3 h package for the zone, and a cashback tier (for example from 200 ₴: 10 %).
- [ ] Start dialog → Тариф → 1 год: the cost box shows two price slices around the rule boundary
- [ ] Start dialog → Пакет: the package shows its end time; start it for a player paid from the balance
- [ ] Player card: balance and bonus went down by the package price (bonus first); "Баланс збігається з журналом ✓"
- [ ] Pause on the map: the VM locks and the launcher countdown stops; Resume: unlocks, end moved by the pause
- [ ] Transfer to another free PC: the old PC locks, the new PC unlocks with the same end time
- [ ] Open-ended session: end it after a few minutes as a guest with "Картка": the ledger (Owner → `/api/ledger`) has a card row
- [ ] Cashier cannot refund or correct; Manager refund takes back the cashback of the refunded amount
- [ ] Audit log shows session.paused / resumed / transferred and package / tariff changes

### Test 10: Bar, Shift and Z-Report (phase 4)
Prepare (Owner or Manager, admin UI → Склад / Stock): two products with a counted stock (for example
"Monster 0,5 л" 75 ₴, stock 10, warn at 3) and one without (coffee). A player with a real balance.
- [ ] Cashier, Каса / Бар without an open shift: the cash and card buttons are off; a cash top-up on
      the player card answers "Спершу відкрийте зміну"
- [ ] Зміна → open a shift with a float of 500 ₴; the shift shows "🙈" for the expected cash (blind)
- [ ] Каса / Бар: tap two products, pay in cash → "Чек №N"; the stock goes down; a third product from
      the player's balance (bonus is not used)
- [ ] A sale of more than the stock is refused ("Не вистачає ... на складі")
- [ ] The cashier has no "Повернути" button; Manager: "Повернути" on the cash receipt, with a reason →
      the receipt shows "Повернено", the stock goes back up
- [ ] Stock: a delivery (+24) by the cashier; a stock count with a reason by the Manager; both in the
      stock log with who and when; "Склад збігається з журналом ✓"
- [ ] Manager: a cash payout with a reason; more than the drawer holds is refused
- [ ] Close the shift with the counted cash: the result shows expected, counted and the variance
- [ ] 📄 Z-звіт: time, bar, top-ups, refunds, bonus, debt, payouts, cash and card, staff table,
      "Звіт збігається з журналом операцій ✓", the audit anchor; Print shows black on white
- [ ] CSV (UA, Excel) opens in Excel with Ukrainian text and numbers in columns; CSV (EN) with commas
- [ ] Manager, Зміна: ledger CSV for this month opens in Excel
- [ ] A closed shift cannot be closed again ("Зміну закрито")
- [ ] Open-ended session of a player with an empty balance: end it with "У борг" → the player card
      shows the debt; "Сплатити борг" in cash; a new open-ended session is refused while a debt is left
- [ ] Owner, Журнал дій → 🔗 Перевірити ланцюжок: "Ланцюжок цілий ✓"
- [ ] UA / EN switch in the top bar: the desk tabs, the player card and the dialogs change language (all screens: Test 12)

### Test 11: Emergency PIN Unlock Is Audited (phase 4)
On the VM (installed with `install-client.ps1`, so `C:\ProgramData\ClubAgent\inbox` exists):
- [ ] `icacls C:\ProgramData\ClubAgent\inbox`: Users have only `(WD,S)`, OWNER RIGHTS `(OI)(IO)(RC)`
- [ ] As the player (standard user): `dir C:\ProgramData\ClubAgent\inbox` is refused
- [ ] Stop the agent (`sc.exe stop ClubAgent`), wait 15 s, unlock with Ctrl+Shift+U + the staff PIN
- [ ] As Administrator: a file `event-*.json` is in the inbox; as the player, `del` of it is refused
- [ ] Start the agent: within 10 s the file is gone and the audit log has `device.emergency_unlock`
      for this PC with "the agent was not responding"
- [ ] The same unlock while the agent runs: `device.emergency_unlock` with "the PC was locked"

### Test 12: Owner Analytics, Currency and Languages (phase 5)
Use the demo (`DEMO_MODE=true docker compose up -d`, 30 days of history) or a club with a few days of data.
- [ ] Owner or Manager → Аналітика: the period chips Сьогодні / 7 днів / 30 днів; the tiles show revenue,
      PC time, bar, average session and bar receipt, occupancy, refunds, bonus, top-ups
- [ ] 30 днів: 30 bars (PC time below, bar on top); hover or tap a bar: the line under the chart shows the day,
      PC time, bar, total and occupancy; Сьогодні: bars per club hour
- [ ] Revenue by payment method: cash + card + balance + bonus + debt = the revenue tile
- [ ] Occupancy by zone: one bar per zone in the zone colour (as on the PC map)
- [ ] Heatmap: 7 rows (Пн–Нд) × 24 hours; evenings light, early mornings dark; "Показати числа" shows the %;
      for "Сьогодні" only today's row is filled
- [ ] The green line under the page: "Збігається з журналом операцій … і з Z-звітами …"; pick a closed shift that
      lies fully in the period: its Z-report (Зміна → 📄 Z-звіт) adds to the analytics with the rows outside shifts
- [ ] ⬇️ CSV (UA, Excel) opens in Excel: summary, days, zones, heatmap, top products; ⬇️ CSV (EN) with commas
- [ ] Cashier and Tech have no analytics tab; `GET /api/analytics` answers 403 for them
- [ ] Phone (or a browser window 390 px wide): the analytics page has no horizontal scroll; the heatmap scrolls
      inside its card
- [ ] Owner → Налаштування → Валюта клубу: EUR → saved; prices, balances, analytics and the Z-report show €;
      a second browser signed in as a cashier shows € within 5 s; the amounts are the same numbers (no conversion);
      a new top-up text in the player card says "€", older rows keep "₴"; the audit log has settings.currency_changed;
      CSV headers say "Сума, €" / "Amount, EUR"; set UAH again
- [ ] UA / EN switch (top bar or login page): every screen (dashboard, map and its menu, sessions, desk, shift,
      stock, devices, players, tariffs, analytics, audit log, staff, settings, dialogs, login) is fully in one
      language; club data (product, tariff, player names) stays as entered
- [ ] Demo: Owner → Журнал дій → 🔗 Перевірити ланцюжок: "перевірено якорів змін: 60" (every seeded closed shift)

### Test 13: Install From the Release Zips (phase 6)
On GitHub: Actions → Release → Run workflow (branch main). On a clean Windows 11 VM and a second VM as the server:
- [ ] The run is green; the artifact `release-dryrun-<commit>` has `ccms-server-…`, `ccms-agent-…`,
      `ccms-launcher-…-win-x64.zip` and `SHA256SUMS`; no release and no tag were created
- [ ] `Get-FileHash <zip>` of each zip gives the SHA-256 of its line in `SHA256SUMS`
- [ ] Each zip has `LICENSE`, `NOTICE.md` and `INSTALL.txt`; the server zip has `server\wwwroot\index.html`
- [ ] Server VM: extract the server zip, `Install.cmd` as Administrator → service `ClubServer` runs, the setup code
      and the enrollment token are printed, `http://localhost:5081` shows the setup screen of the admin UI
- [ ] Client VM: extract the agent zip and the launcher zip into one folder (`agent\`, `launcher\`, `Install.cmd`),
      run `Install.cmd -ServerUrl "http://<server>:5081" -EnrollmentToken "<token>"` as Administrator →
      `Get-Service ClubAgent` is Running, the PC is online and approved in the admin UI
- [ ] After a logon the lock screen (launcher) starts and is locked
- [ ] If Windows marks the extracted files as downloaded and refuses them: `Get-ChildItem -Recurse | Unblock-File`
      in the folder, then run `Install.cmd` again (note it in the release notes if this happens)
- [ ] A pushed tag `v0.0.1-test` (in a fork) creates a pre-release with the three zips and `SHA256SUMS`;
      the agent of that build reports the version `0.0.1.0` (Devices: agent version)

### Test 14: Lock Screen in Ukrainian and English (phase 6)
- [ ] Before the PC ever reaches the server (new install, server off): the lock screen is Ukrainian; no
      `C:\ProgramData\ClubAgent\client-settings.json`
- [ ] Start the server: within 10 s `client-settings.json` exists (`"language": "uk"`, `"currency": "UAH"`)
- [ ] Owner → Налаштування → Мова екранів блокування → English → within 10 s the lock screen is English
      (title, hint, staff hint, UA button); the audit log has `settings.language_changed`
- [ ] Owner → currency EUR: a player balance on the session screen shows `€`; set UAH again
- [ ] Press the UA / EN button on the lock screen: the texts switch at once; start and end a session: the PC
      locks again in the club language
- [ ] Ctrl+Shift+U: the PIN dialog, its buttons and its messages are in the current language
- [ ] Stop the server (not the agent): the lock screen keeps the last club language
- [ ] Every text on every screen is in one language (no Ukrainian word on the English screen and back)

### Test 15: Game Catalog and Game Grid (phase 6)
Prepare (Owner or Manager → Ігри / Games): three entries for the PC's zone: a program (for example Notepad,
`C:\Windows\System32\notepad.exe`, process `notepad`), a Steam game the PC has installed (AppID, process name),
and an Epic or Battle.net link; one more entry only for another zone and one inactive entry.
- [ ] Within 30 s `C:\ProgramData\ClubAgent\games.json` lists only the active entries of this PC's zone
- [ ] Start a session: the session screen shows the grid with the tiles (initials on their colours), the category
      chips and the search box; the entry of the other zone and the inactive one are not there
- [ ] Type part of a name: the grid filters at once; a category chip filters; "All" shows everything again
- [ ] Click the program tile: it starts; the top bar shows "Now: …"; within 10 s the PC map shows 🎮 and the name
- [ ] Close the program: within 15 s the map shows no game
- [ ] Steam tile: Steam opens the game (`steam://rungameid/…`); the map shows the game while its process runs
- [ ] Epic / Battle.net tile: the launcher opens the game
- [ ] Change the path of the program entry in the admin UI to a wrong path: the tile shows an error message,
      nothing else starts; the audit log has `game.updated` with the old and new path
- [ ] End the session: the PC locks; the map shows no game; Analytics → Top games lists the games with hours
- [ ] Stop the server: the grid still works with the last catalog (games.json stays)
- [ ] With the PC locked, clicking is not possible (the lock screen covers the grid)

### Test 16: Player Login on the Lock Screen (phase 6)
Prepare: a player with a PIN (Players → 🔑) and 100 ₴ on the balance; the PC's zone has a tariff (for example
60 ₴/h) and a package on sale now. The PC is locked without a session.
- [ ] The lock screen shows the login form: user name, PIN or password, "How to pay" with "From my balance",
      1 / 2 / 3 h with prices in the club currency and the package with its end time; the tariff price now
- [ ] Wrong PIN: "Wrong username or password"; an unknown user name gives the same text
- [ ] Log in with "From my balance": within 3 s the PC unlocks; the session screen shows the player name, the
      balance (and bonus), the tariff, "TIME FROM THE BALANCE" and about 1:40:00 left (100 ₴ at 60 ₴/h)
- [ ] Admin UI: the PC map shows the PC busy with the time left; Sessions shows the badge "from the balance";
      the audit log has `player.login` and `session.started` ("from the balance, until about …")
- [ ] Top up the player by 60 ₴ at the desk: within 10 s the remaining time grows by about 1 h
- [ ] Wait until 10 minutes are left (or give a player only 11 ₴): a sound and a small warning window on top of
      a game in borderless mode; again at 5 minutes and at 1 minute ("Save your game")
- [ ] When the time is used up the PC locks; the balance is 0, the debt 0; "Баланс збігається з журналом ✓"
- [ ] Log in with "2 h": the balance goes down by the price at once; "⏹ END THE SESSION" → Yes: the PC locks,
      unused time is not refunded, the audit log has `player.logout`
- [ ] Five wrong PINs for one player (on one or two PCs): the sixth try, also with the right PIN, says "Too many
      failed attempts. Try again in 15 min"; the audit log has `player.login_locked`; another player still logs in
- [ ] Ten wrong logins with different names on one PC: that PC refuses logins for 15 minutes, another PC works
- [ ] Stop the agent (`sc.exe stop ClubAgent`): the lock screen says the agent does not answer and hides the
      login form; start the agent: the form comes back
- [ ] As the player, run a small program that creates the pipe `\\.\pipe\ClubAgent.Launcher` while the agent
      runs: it fails (access denied / pipe busy)
- [ ] A session started by the cashier for a guest: the PC unlocks without a login; the session screen shows
      "Guest", the tariff and the time; there is no "End the session" button

### Test 17: Clean Session Teardown (phase 6)
Prepare: a Steam game and Notepad in the catalog; Discord and Chrome installed. As Administrator check
`C:\ProgramData\ClubAgent\teardown.json` (cleanup off) and `icacls C:\ClubAgent` (Users: RX only).
- [ ] As the player: `echo x > C:\ClubAgent\test.txt` is refused (access denied)
- [ ] Start a 2-minute session; start Notepad and the Steam game from the grid, start Discord and Chrome yourself;
      open Task Manager as Administrator (Ctrl+Alt+Del → switch user) to watch the processes
- [ ] Pause the session in the admin UI: the PC locks, nothing is closed; resume: the games still run
- [ ] Let the time run out: within 5 s Notepad, the game, Steam (with steamwebhelper), Discord and Chrome are
      closed; Explorer and the lock screen still run; the audit log has `device.session_teardown` with the names
- [ ] A second Windows user logged in at the same time (fast user switching) keeps its Chrome
- [ ] Turn the cleanup on: in `C:\ClubAgent\appsettings.json` set `"Enabled": true` with the path
      `%LOCALAPPDATA%\\Google\\Chrome\\User Data`, `Restart-Service ClubAgent`; sign in to a site in Chrome,
      end the session: Chrome opens without the login next time; the audit log says "cleanup removed … file(s)"
- [ ] As the player create a junction inside the cleaned path to a folder with a test file
      (`mklink /J "%LOCALAPPDATA%\Google\Chrome\User Data\Jump" C:\Users\Public\Documents`); end a
      session: the test file in `C:\Users\Public\Documents` is still there
- [ ] Add `C:\Windows` to `Paths` (not to the roots): nothing there is deleted (the agent log and the audit
      log show no error; the path is skipped)
- [ ] Stop the agent during a session and wait 20 s: the PC locks (agent silent), but nothing is closed; start
      the agent again: the session continues
- [ ] Reinstall the client (`Install.cmd` again): the changed `Teardown` section stays

### Test 18: Shutdown, Reboot and Wake-on-LAN (phase 6)
Prepare: Wake-on-LAN on in the BIOS and in Windows (see ClubDeployment.md, Wake-on-LAN); the server's
`Power:WakeBroadcast` set to the broadcast address of the PC network; the PC online.
- [ ] Admin UI → Devices: the MAC column shows the MAC address of the card in the club LAN (compare with
      `Get-NetAdapter | Select Name, MacAddress` on the PC)
- [ ] As a Cashier: the PC map menu and the Devices tab have no reboot, shutdown or start; the API answers 403
      for `POST /api/devices/{id}/power` and `power_forbidden` for the command `restart`
- [ ] As Tech: PC map → the PC → 🔄 Reboot → the dialog names the PC; Cancel sends nothing; Reboot: the PC
      restarts within 10 s and comes back online; the audit log has `device.power` "Reboot sent to …"
- [ ] Start a session, then ⏻ Shut down: the dialog warns that the session goes on and is billed; confirm:
      the PC shuts down within 10 s; the session still runs in the admin UI
- [ ] The PC is offline: PC map → ⚡ Start (WoL) → confirm: the PC starts within a minute; the audit log has
      "Wake-on-LAN sent to … (MAC) via <broadcast>:<port>"
- [ ] Set a wrong `Power:WakeBroadcast` (for example `abc`), restart the server: the log says that Wake-on-LAN
      is off; the start button answers "Wake-on-LAN is off: check Power:WakeBroadcast …"; set it back
- [ ] Shut down a PC that the server cannot reach any more for a while: the queued command runs when the PC is
      back within an hour; after an hour it expires and never runs
- [ ] UA / EN: the dialog, the buttons and the toasts are in the selected language

### Test 19: Log Off and Login Lockouts (phase 7)
Prepare: a player with a PIN; the PC locked without a session; a second Windows user is not signed in.
- [ ] Sign in to Windows as the player account, start a session, open Notepad with unsaved text
- [ ] Admin UI → PC map → the PC → 🚪 Log off: within 5 s Windows signs the player out (Notepad closes without a
      question); the command shows "Sent to the lock screen" in the command result; after the next sign-in the lock
      screen does not log off again
- [ ] Stop the launcher (staff PIN, then close it) and send Log off: nothing happens (the agent alone cannot log off);
      start the launcher within a minute: it does not log off either (the request is older than the lock screen)
- [ ] Five wrong PINs for the player: the lock screen says "Too many failed attempts"; Manager → Players →
      "Login lockouts on the PCs" lists @player; "Clear the lockout" → the player logs in at once; the audit log has
      `player.login_lockout_cleared` for the account
- [ ] Ten wrong logins with invented names on one PC: the list shows the PC and "unknown name (no account)" rows
      (never the typed text); clear the PC → logins work on it again
- [ ] A cashier has no lockout list (and the API answers 403)

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

