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
- [ ] A new lock from the admin UI locks the VM again, also when the PC had no session (see Test 25.2)

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

### Test 20: Reservation Hold on the Lock Screen (phase 7)
Prepare: two players with PINs and 200 ₴ each; the PC locked without a session; the club time zone right.
- [ ] Cashier → Бронювання → click the row of the PC 10 minutes from now → player A, 1 h, deposit 50 ₴ → the bar is
      amber ("ПК утримується"); the player card shows −50 ₴ with "Депозит броні"
- [ ] Within 10 s the lock screen shows "ЗАБРОНЬОВАНО ДЛЯ <A's nickname> НА HH:MM" (club time); EN: "RESERVED FOR ...";
      no phone, no surname
- [ ] Player B logs in on the PC: "Цей ПК заброньовано для A на HH:MM ..."; the PC stays locked
- [ ] The cashier starts a session on the PC: refused; the dialog shows the hold; a Manager ticks the override and
      starts it → the audit log has `reservation.hold_overridden`; end that session
- [ ] Player A logs in with "1 h": the PC unlocks; the reservation is "Прийшов", the deposit "зараховано на сесію"
- [ ] A second reservation 10 minutes from now for a walk-in name; nobody comes: 15 minutes after the start it is
      "Не прийшов" and the PC map shows no reservation any more
- [ ] UA / EN: the lock screen texts, the timeline, the dialogs and the errors are in one language

### Test 21: Bar Order From the Lock Screen (phase 7)
Prepare: a player with 200 ₴ and 100 bonus; products with stock (one with stock 1) and coffee without stock;
the cashier signed in on the admin PC with the sound on; an open shift.
- [ ] Start a session for the player (or log in on the lock screen): the session screen shows "🥤 ЗАМОВИТИ"
- [ ] The panel lists only active products in stock; + stops at the stock (1) and at 10; the total grows in the club
      currency; above the most per order (500 ₴) + does nothing and the panel says so
- [ ] "З мого балансу (200.00 ₴)" → ЗАМОВИТИ: "Замовлення прийнято"; the line under the buttons says "Замовлення №N:
      прийнято · … · оплачено з балансу"; the player card shows −total ("Замовлення бару (утримано)"); the bonus is
      unchanged
- [ ] Within 5 s the admin UI plays a short tone and the tab "Замовлення" shows the badge 1; a second order from the
      same PC is refused ("У вас уже є відкрите замовлення")
- [ ] The cashier clicks "Готується": the lock screen line says "готується" within 10 s and the player cannot cancel any
      more; "Видано": a receipt appears in Каса / Бар (balance), the stock goes down, the Z-report counts it
- [ ] A guest session: only "Оплачу на касі"; the cashier delivers with "Видано: готівка" → a cash receipt
- [ ] A new order cancelled by the player on the lock screen returns the money at once; a cancel by the cashier too
- [ ] Owner → Замовлення → settings: orders off → the button disappears from the lock screens within 30 s
- [ ] UA / EN: the panel, the status line and the errors are in the club language

### Test 22: Event on the Lock Screen (phase 8)
Prepare: two PCs of one zone with the agent and the lock screen, locked without a session; two players with PINs and
150 ₴ each; a Manager and a Cashier account; the club time zone right.
- [ ] Manager → Події → "+ Нова подія": the two PCs, start in 20 minutes, 30 min, teams of 1, PC time 20 ₴, entry fee
      50 ₴ → the event card says "Заплановано"; a reservation on one of the PCs at that time is refused
- [ ] Cashier → the event → register player A and player B: each player card shows −50 ₴ ("Внесок за участь")
- [ ] 15 minutes before the start the lock screens show "ПОДІЯ «name» О HH:MM" (EN: "EVENT «name» AT HH:MM")
- [ ] Player A logs in on the PC: "Цей ПК зайнятий для події ..."; the PC stays locked
- [ ] At the start both PCs unlock within 10 s; the session panel shows "🏆 Подія: name" and the time left counts down
      to the common end; each player card shows −20 ₴ for the PC time
- [ ] Manager → "Сітка за реєстрацією" → one match (the final); Cashier clicks it, chooses player B → "Переможець: B"
- [ ] At the end both PCs lock; the event is "Завершено"; the Z-report shows "Внески за участь у подіях: залишено
      клубу 100.00 ₴"; `GET /api/ledger/holds` says `eventFees.ok = true`
- [ ] A second event with an entry fee, one player registered, cancelled by the Manager: the fee is back on the
      balance; the audit log has `event.cancelled` without the event name
- [ ] UA / EN: the lock screen texts, the tab, the dialogs and the errors are in one language

### Test 23: Club Game Account on the Lock Screen (phase 8)
Prepare: a test account of a platform that allows a login with a password and has **no** two-step verification (never a
real customer account); one PC with the agent and the lock screen in kiosk mode; on that PC, in
`C:\ClubAgent\appsettings.json`, `AccountPool:Platforms:steam` with the client path and `-login {login} {secret}` (or
the arguments of your client); restart the agent; a game of the catalog with "Club account for the game: Steam".
- [ ] `C:\ProgramData\ClubAgent\account-pool.json` lists the platform; the agent log says "1 platform(s) can log in"
- [ ] Manager → Акаунти ігор → add the account: the row shows the masked login and "збережено"; no screen shows the login
      or the secret again; a Cashier has no add or change buttons (and the API answers 403)
- [ ] Without a session the tile does nothing (the PC is locked). Start a session; click the tile: the lock screen says
      "Клубний акаунт ...: вхід у Steam…", the client starts and logs in, then the game starts
- [ ] The admin UI shows the account "зайнятий" on this PC with "вхід виконано"; the audit log has
      `game_account.leased` and `game_account.delivered` with the masked login only
- [ ] Click the tile again: the game starts at once (no second login, no second delivery)
- [ ] A second PC opens the game while the pool has no free account: "Усі клубні акаунти Steam зараз зайняті"
- [ ] Copy `Cms.Launcher.exe` to the Desktop of the player and run it (as an administrator test): its account request is
      refused (the agent log says "refused (only C:\ClubLauncher\Cms.Launcher.exe may ask)")
- [ ] End the session: the teardown closes the client; the account is "вільний" again; the next player on the PC does
      not find the client logged in (check the "remember me" setting of the client)
- [ ] Remove the account: the database row has no login or secret any more (only the masked login)
- [ ] UA / EN: the lock screen messages and the tab are in one language

### Test 24: Club Theme and Logo on the Lock Screen (themes phase)
Prepare: one PC with the agent and the lock screen in kiosk mode; the Owner account; a PNG logo (for example 300 × 120
px, below 512 KB); a GIF file; screens of 1366 × 768 and 1920 × 1080 if the club has both.
- [ ] Fresh install or upgrade: `C:\ProgramData\ClubAgent\client-settings.json` has `"theme": "cyber"`; the lock screen
      looks like Cyber; the admin UI Settings shows "Кібер" selected
- [ ] Owner → Налаштування → Тема клубу → "Професійна темна" → "Зберегти тему": within 10 s the lock screen (locked and
      in a session) changes to Pro Dark without a restart of the launcher; an admin UI open on another PC changes too
- [ ] Repeat with "Світла" and "Аркада": the title, the buttons and the countdown are readable; the Ukrainian letters
      ґ є і ї show in the theme font; nothing is cut off at 1366 × 768 and 1920 × 1080
- [ ] Accent `#E0457B` → buttons, borders and the countdown use it; accent `#00F0FF` with "Світла" is refused
      ("Колір має замалий контраст ...")
- [ ] Upload the PNG: within 10 s `club-logo.img` exists, the login panel and the session top bar show the logo instead
      of "PC CLUB"; the agent log says "Club logo Downloaded"; the audit row `settings.logo_changed` has only 12 hex
      characters of the hash
- [ ] Upload the GIF: refused ("PNG або JPG"); the PC keeps the old logo
- [ ] As an administrator, replace `club-logo.img` with other bytes: the lock screen keeps the logo it already shows; within
      about 60 s the agent writes the right file again (agent log "Club logo Downloaded"). Replace the file again and
      restart the launcher at once: it shows "PC CLUB" (no error window) until the agent repairs the file, then the logo
- [ ] Upload a logo of 1024 × 8 px: the lock screen shows it in one line, no slow start or memory jump of the launcher
- [ ] Remove the logo in Settings: the file is deleted and the lock screen shows "PC CLUB"
- [ ] Stop the server, restart the PC: the lock screen shows the last theme and logo
- [ ] UA / EN on the lock screen: the texts stay in one language in every theme

### Test 25: Fixes of the Windows Test of 2026-11-27 (v0.9.1)
Bench: the server VM (service ClubServer) and one club PC in kiosk mode, both updated to v0.9.1. Use an administrator
PowerShell on the club PC (Ctrl+Alt+Del > Switch user > `ClubAdmin`, or the guest agent as SYSTEM). The agent log is
`C:\ProgramData\ClubAgent\logs\agent.log`. Never print a token, a key, a PIN or a password into a log or a screenshot.

**25.1 The agent does not stop when the server does not answer (defect 1)**
1. On the server: `Stop-Service ClubServer`. Restart the club PC and wait 3 minutes.
- [ ] `Get-Service ClubAgent` shows `Running`; the lock screen shows the locked screen, not "АГЕНТ НЕ ВІДПОВІДАЄ".
- [ ] agent.log has "Registration: the server did not answer in time" or "Registration failed (server unreachable?)",
      with a delay that grows to 60 s; no line stops the agent.
2. On the server: `Start-Service ClubServer`.
- [ ] Within about 70 s the PC is online in the admin UI; agent.log has "Registered device".
- [ ] `sc.exe qfailure ClubAgent` shows RESET_PERIOD 86400 and RESTART 5000, 5000, 30000; `sc.exe qfailureflag ClubAgent`
      shows TRUE. On the server the same for `ClubServer`.
3. `Stop-Process -Name Cms.Agent.Service -Force`.
- [ ] Within 10 s `Get-Service ClubAgent` shows `Running` again (restart action).

**25.2 A lock from the admin UI ends a staff PIN unlock (defect 2)**
1. The PC is locked and has no session. Press Ctrl+Shift+U and enter the staff PIN.
- [ ] The PC is unlocked. Within 10 s the admin UI (PC map) shows "🔓 PIN" (title "Розблоковано PIN персоналу на екрані ПК").
2. Admin UI: Lock (or `POST /api/devices/{id}/commands {"type":"lock"}`).
- [ ] Within 5 s the screen shows "[ ЗАБЛОКОВАНО ]"; `state.json` has a `lockGeneration` one higher; agent.log has
      "Locked by the server (lock N; ...)"; the admin UI shows 🔒.
3. Repeat steps 1 and 2 once more.
- [ ] The second lock also locks the screen.

**25.3 A club account on a standard install (defect 3)**
1. Check that the agent is LocalService: `(Get-CimInstance Win32_Service -Filter "Name='ClubAgent'").StartName` gives
   `NT AUTHORITY\LocalService`. Run Test 23.
- [ ] The tile logs in with the club account; no "pipe_client_untrusted" and no raw code on the screen.
- [ ] The copy of `Cms.Launcher.exe` on the Desktop is refused: agent.log has "refused: OtherProgram"; the screen
      says "Клубний акаунт видається лише встановленому екрану блокування цього ПК".
- [ ] agent.log has no "RevertToSelf" line, and `Get-Service ClubAgent` stays `Running`.

**25.4 The update of a kiosk PC (defect 4)**
1. The PC runs v0.9.0 in kiosk mode; `ClubUser` is signed in; no session runs. Extract the v0.9.1 zips into a new
   folder and run as SYSTEM or Administrator: `Install.cmd -ServerUrl "http://<server>:5081" -EnrollmentToken "<token>" -KioskMode`.
- [ ] Exit code 0. The last lines say "RESULT: OK - the club PC software is updated", the agent version 0.9.1.0 and
      "ClubUser was signed out; the automatic sign-in starts the new lock screen".
- [ ] `ClubUser` signs in again by itself; the new lock screen shows "[ ЗАБЛОКОВАНО ]". `C:\ClubAgent.new`,
      `C:\ClubAgent.old`, `C:\ClubLauncher.new` and `C:\ClubLauncher.old` do not exist, and no folder `ccms-update-*`
      stays in `C:\Windows\Temp`, `C:\Windows\SystemTemp` or the TEMP folder of the administrator (see 26.7).
- [ ] `C:\ClubAgent\appsettings.json` keeps `Teardown` and `AccountPool:Platforms`; `AccountPool:LauncherPath` is
      `C:\ClubLauncher\Cms.Launcher.exe`.
2. Failure test. In an administrator PowerShell keep the lock screen file open for reading only:
   `$f = [IO.File]::Open('C:\ClubLauncher\Cms.Launcher.exe', 'Open', 'Read', 'Read')`. Run the same `Install.cmd` in a
   second window.
- [ ] Exit code 1 and "UPDATE FAILED: Could not copy the lock screen files ... The old version is restored and runs
      again", then "RESULT: FAILED".
- [ ] `Get-Service ClubAgent` is `Running`; `(Get-Item C:\ClubAgent\Cms.Agent.Service.exe).VersionInfo.FileVersion` is
      the old version; `ClubUser` signed in again with the old lock screen. Then `$f.Close()` and run the update again: OK.

**25.5 A killed lock screen comes back (defect 5)**
1. The PC is locked. In the admin session: `Get-Process Cms.Launcher | Format-Table Id, SessionId, MainWindowHandle, StartTime`.
- [ ] Two processes run in the session of `ClubUser`: the watchdog (MainWindowHandle 0) and the lock screen.
2. `Stop-Process -Id <id of the lock screen> -Force`, then switch back to `ClubUser`.
- [ ] The lock screen is back within 2 s (a new StartTime) and shows the locked screen.
3. `Stop-Process -Id <id of the watchdog> -Force`.
- [ ] Within 5 s a new watchdog process runs.
4. `Stop-Process -Name Cms.Launcher -Force` (both at once).
- [ ] Within 60 s both are back (scheduled task `ClubLauncherWatchdog`; `Get-ScheduledTask ClubLauncherWatchdog` exists).
5. `Disable-ScheduledTask ClubLauncherWatchdog`, then `Stop-Process -Name Cms.Launcher -Force`, wait 30 s.
- [ ] agent.log has the warning "The lock screen does not answer ... while this PC must be locked"; the admin UI shows
      ⚠️ on the PC ("Екран блокування не відповідає: перевірте ПК").
6. `Enable-ScheduledTask ClubLauncherWatchdog`.
- [ ] Within 60 s the lock screen is back; agent.log has "The lock screen answers again"; the ⚠️ is gone.

**25.6 The agent log (defect 6)**
- [ ] `C:\ProgramData\ClubAgent\logs\agent.log` exists; `icacls C:\ProgramData\ClubAgent\logs` lists only SYSTEM,
      Administrators and LOCAL SERVICE.
- [ ] It has "ClubAgent 0.9.1.0 started", "Club accounts: N platform(s) can log in", after a logo change "Club logo
      Downloaded"; it has no "Sending HTTP request" lines.
- [ ] `Get-EventLog -LogName Application -Source Cms.Agent.Service -Newest 5` works; the Application log has no new
      "The source Cms.Agent.Service was not found".
- [ ] `Select-String -Path C:\ProgramData\ClubAgent\logs\*.log -Pattern '<token>','<staff PIN>','<account secret>'`
      finds nothing (type the values only into this command; do not keep the command history).

**25.7 The enrollment token does not stay (defect 7)**
- [ ] After the update of 25.4 the installer said "This PC is registered already: the enrollment token is not stored",
      and `Test-Path C:\ProgramData\ClubAgent\private\enrollment-token.txt` is `False`.
- [ ] A new install: the file exists only until the PC is online; agent.log has "Deleted the enrollment token file".

**25.8 No window above the locked screen (defect 8)**
1. Start a session; start the catalog entry of `calc.exe` (the "Open with" dialog opens). Pause the session in the admin UI.
- [ ] Within 5 s the dialog is gone from the screen; the locked screen is on top and takes the clicks (see 26.2).
- [ ] Ctrl+Shift+U opens the staff PIN dialog above the lock screen; it takes the input; the PIN works.

**25.9 One language, texts and layout (defect 9)**
- [ ] End the session on the PC: the question is the own dialog of the lock screen in the club theme with
      "[ ТАК ]" / "[ НІ ]" (English: "[ YES ]" / "[ NO ]"); Esc is "No". A wrong staff PIN shows the own dialog too.
- [ ] A catalog entry with a missing program says "Не вдалося запустити «…»: програму не знайдено на цьому ПК", not the
      Windows text of the error. No screen shows a code such as `pipe_client_untrusted` or "Помилка сервера (500)".
- [ ] Bar panel: "+" and "−" show the signs. With "[ СКАСУВАТИ ЗАМОВЛЕННЯ ]" visible, "[ ЗАКРИТИ ]" is fully visible.
      "+" at the stock limit says "Більше цього товару в одному замовленні не можна"; "+" above the amount limit says
      "Не більше 500.00 ₴ за одне замовлення".
- [ ] Set the display to 1024 × 768, then 1280 × 800. On the locked screen with a reservation or event line and a
      two-line login error the UA / EN button is in the top right corner and the login button is visible. In a
      session with less than 10 minutes the warning line is below the top bar and does not cover "> ЗАЛИШИЛОСЬ ЧАСУ".
- [ ] Start Notepad from the grid; the admin UI shows "Notepad". Log off from the admin UI: within 10 s the admin UI
      shows no current game; the session goes on.
- [ ] After the update from 0.9.0, `C:\ProgramData\ClubAgent\client-settings.json` has `"theme"` within 10 s.

**25.10 Discovery on a server with two network cards (known issue)**
1. Give the server VM a second network card with the default route on it. Install a club PC without `-ServerUrl`.
- [ ] The server log says "Sent discovery response: http://<address on the club LAN>:5081"; agent.log says "Using
      discovered server: http://<address on the club LAN>:5081".

**25.11 The lock state after a start (known issue)**
- [ ] Restart `ClubServer`: within 10 s the admin UI shows 🔒 for every locked PC that is online (not empty).
- [ ] Restart a club PC that has no session: within 10 s after it is online, the admin UI shows 🔒.

### Test 26: Fixes of the Windows Re-test of 2026-11-27 Night (v0.9.1)
Bench as in Test 25 (kiosk mode, the agent as LocalService). The guard of the locked screen works every second, only
while the PC is locked: in a running session it does nothing. Version: the release of the tag `v0.9.1-beta.1` shows
`0.9.1.0` (file version, agent log, installer); a dry-run build (`dryrun-<commit>`) shows `0.0.0.0` on purpose.
Prepare these catalog entries (zone of the club PC):
- "Calc" (`C:\Windows\System32\calc.exe`): on a PC without the Store Calculator it opens the Windows "Open with" dialog.
- "Topmost test": as administrator copy `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` to
  `C:\Users\Public\explorer.exe` (a program of the player with the NAME of a Windows program). Exe
  `C:\Users\Public\explorer.exe`, arguments
  `-NoProfile -WindowStyle Hidden -Command "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; $f.TopMost = $true; $f.Text = 'TOPMOST TEST'; [Windows.Forms.Application]::Run($f)"`.
- Notepad with the process name `notepad` (25.9).

**26.1 The staff PIN dialog after the sign-in (re-test defect 1)**
1. The PC is locked and has no session. In the admin session: `logoff <session id of ClubUser>`. The automatic
   sign-in starts the lock screen. At once, before any click, press Ctrl+Shift+U.
- [ ] The PIN dialog is fully drawn above the locked screen and the PIN box has the cursor. Type the PIN and press
      Enter (no click): the PC unlocks. Repeat 3 times.
2. Press Ctrl+Shift+U, wait 15 s with the dialog open, then type the PIN.
- [ ] The dialog keeps the keys the whole time (the guard does not give the activation back to the main window).

**26.2 No window above the locked or paused screen (re-test defect 2, security review)**
1. Start a session; start "Calc" (the "Open with" dialog opens) and Notepad (type a line). Pause the session.
- [ ] Within 5 s the "Open with" dialog is gone; in the admin session `Get-Process OpenWith -ErrorAction
      SilentlyContinue | Where-Object SessionId -eq <session of ClubUser>` finds nothing within 10 s.
- [ ] Without a click, Ctrl+Shift+U opens the PIN dialog in front and it takes the PIN (Cancel it).
- [ ] Notepad still runs (`Get-Process notepad`). Resume: Notepad is there with its line; "Зараз: Notepad" is on the
      top bar.
2. In the session start "Topmost test" (the window "TOPMOST TEST" is above everything, also in the session: that is
   correct). Pause the session.
- [ ] Within 3 s the window is no longer on the screen (pushed down: not top-most, minimized; in a pause the program
      is closed only when the push-down does not hold or when it comes back above the locked screen for the third
      time): a program with the name of a Windows program is not trusted (3aa3619 left it above the locked screen). The real Windows programs (`C:\Windows\explorer.exe`, the
      touch keyboard, the IME) are never touched; this is in the unit tests (LockScreenGuardTests).
- [ ] End the session: within 15 s after the end the process `explorer` of `C:\Users\Public` is ended (teardown, or the
      guard 10 s after the lock).
3. Resume, start "Calc" again, then end the session in the admin UI (the "Open with" dialog is not in the kill list).
- [ ] The teardown runs as before (audit `device.session_teardown`). Within 15 s after the end the "Open with" dialog
      is gone too (a locked PC without a session closes every program of ClubUser after 10 s).
4. Start a session; start "Topmost test" and Notepad. Stop the agent (`Stop-Service ClubAgent`) and wait 20 s.
- [ ] The PC locks (agent silent); "TOPMOST TEST" is no longer on the screen within 5 s; Notepad still runs (nothing is
      closed while the PC cannot know whether the session goes on).
      `Start-Service ClubAgent`: the session goes on.

**26.3 Ctrl+Shift+U at the first press (re-test defect 3)**
1. Start a session and pause it. Click once on the paused screen, then press Ctrl+Shift+U once.
- [ ] The PIN dialog opens at the first press. Cancel it. Hold Ctrl+Shift+U for 3 s: only one dialog opens.
2. Resume. In the session press Ctrl+Shift+U in the lock screen window (not in a game).
- [ ] The PIN dialog opens as before (a game in the foreground gets the keys: the hook is on only while locked).

**26.4 The agent log names the real event (re-test defect 4)**
1. In a session start and close Notepad; end the session; then do a staff PIN unlock.
- [ ] agent.log has "Reported the start of game <id>", "Reported the stop of game <id>", "Reported the teardown of the
      ended session at ... (N program(s) closed)", and only for the PIN unlock "Reported the emergency unlock with the
      staff PIN at ...".

**26.5 The newest lock wins in the admin view (re-test defect 5)**
1. The PC is locked, no session. Staff PIN unlock (Ctrl+Shift+U). Wait until `GET /api/devices` shows
   `isLocked=false`, `lockScreen=staff_unlock` for the PC.
2. Lock it in the admin UI and read `GET /api/devices` every 0.5 s for 15 s.
- [ ] After the first `isLocked=true` the PC never shows `isLocked=false` again; the map shows 🔒, never "🔓 PIN".
      Repeat once more (lock 2).

**26.6 "Зараз: <game>" stays (re-test defect 6)**
1. In a session start Notepad; the top bar shows "Зараз: Notepad". In the admin session end only the lock screen
   (25.5 step 2): `Get-Process Cms.Launcher | Where-Object MainWindowHandle -ne 0 | Stop-Process -Force`.
- [ ] Within 5 s the new lock screen shows the session with "Зараз: Notepad"; the admin UI shows the current game Notepad.
- [ ] Close Notepad: within 15 s the top bar has no game line and the admin UI shows no current game.
2. Start Notepad again; pause the session for 1 minute; resume.
- [ ] "Зараз: Notepad" is on the top bar all the time after the resume.

**26.7 The installers never follow a junction (security review)**
1. As a standard Windows user (not ClubUser, not an administrator): create `C:\Users\Public\victim\keep.txt`, then
   `mklink /J C:\ClubAgent.new C:\Users\Public\victim`. Run the update of 25.4 as SYSTEM.
- [ ] "RESULT: OK". The installer warns "Not removed: C:\ClubAgent.new (it is a junction or a symbolic link)";
      `C:\Users\Public\victim\keep.txt` is still there. Remove the junction by hand: `rmdir C:\ClubAgent.new`.
- [ ] During the run a folder `ccms-update-<random>` exists in the TEMP folder of SYSTEM (`C:\Windows\SystemTemp` or
      `C:\Windows\Temp`); `icacls` shows only SYSTEM and Administrators; it is gone after the run.
2. A PC without the club software: as a standard user `mkdir C:\ClubAgent`, then run `Install.cmd` as administrator.
- [ ] Exit code 1: "C:\ClubAgent cannot be used: its owner (...) is not SYSTEM or an administrator ...". The same with
      `mklink /J C:\ProgramData\ClubAgent C:\Users\Public\victim` instead: "is a junction or a symbolic link" (the
      texts since v0.9.2: 27.5 step 2).
3. As administrator create `mklink /J C:\Users\ClubUser\AppData\Local\jump C:\Users\Public\victim`. Run
   `uninstall-kiosk.ps1` and delete the ClubUser account and profile.
- [ ] The profile folder is gone; `C:\Users\Public\victim\keep.txt` is still there.
4. Server PC: run the server update (`Install.cmd`).
- [ ] `icacls C:\ClubServer` lists only SYSTEM (F), Administrators (F) and Users (RX); the server runs.

### Test 27: Fixes of the Windows Re-test of 2026-11-27 Late (v0.9.2)
Bench as in Test 26 (kiosk mode, the agent as LocalService, the catalog entries "Calc", "Topmost test" and Notepad).
Version: the release of the tag `v0.9.2` shows `0.9.2.0`; a dry-run build shows `0.0.0.0`. Update the server and the
club PC first (25.4 with `-KioskMode`): the update ends with "RESULT: OK" and shows no warning "did not sign in again".

**27.1 No "Open with" dialog above the paused or locked screen (re-test defect 1, HIGH)**
1. Start a session; start "Calc" (the Windows 11 "Open with" dialog opens) and Notepad (type a line). Pause the session.
- [ ] Within 3 s the "Open with" dialog is gone. In the admin session
      `Get-Process OpenWith -ErrorAction SilentlyContinue | Where-Object SessionId -eq <session of ClubUser>` finds
      nothing within 5 s (WM_CLOSE at once; the process is ended 2 s later when the window stays).
- [ ] Without a click, Ctrl+Shift+U opens the PIN dialog in front; type 3 digits: they arrive in the PIN box; Esc
      closes the PIN dialog (not another window).
- [ ] Notepad still runs with its line. Resume: "Зараз: Notepad" is on the top bar.
2. Resume, start "Calc" again, then end the session in the admin UI.
- [ ] Within 3 s after the end the "Open with" dialog is gone, and it does not come back (100 s, every 5 s).
3. Start a session and start "Calc". Do not pause.
- [ ] The "Open with" dialog stays open in the running session (the guard is off while unlocked).
4. Regression: repeat 26.2 steps 2 and 4 ("TOPMOST TEST" is pushed down in a pause, Notepad stays when the agent stops).
5. Regression: on the locked screen tap or click into the user name box with the touch keyboard on (Settings > Time &
   language > Typing > Touch keyboard): the touch keyboard opens and types.

**27.2 The uninstaller deletes the profile of ClubUser (re-test defects 2 and 4)**
1. The PC is locked, ClubUser is signed in. Press Ctrl+Alt+Del > Switch user > ClubAdmin (27.3). In ClubAdmin,
   `query user` shows ClubUser as "Disc" (no SESSIONNAME). As administrator create the junction
   `mklink /J C:\Users\ClubUser\AppData\Local\jump C:\Users\Public\victim` (with `keep.txt` in `victim`).
2. Run `uninstall-kiosk.ps1` in an elevated PowerShell of ClubAdmin and answer `y`.
- [ ] Step 4 shows "Signed out the session <id> of ClubUser" and "User 'ClubUser' removed"; no "Could not remove" and
      no "was not removed completely". `$LASTEXITCODE` is 0.
- [ ] `query user` lists no ClubUser; `Test-Path C:\Users\ClubUser` is False; `C:\Users\Public\victim\keep.txt`
      is still there; `Get-ScheduledTask ClubKioskAutoLogon, ClubLauncherWatchdog -ErrorAction SilentlyContinue`
      finds nothing; `reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon` = 0.
3. Install the kiosk again (Test 13 / 25.4 with `-KioskMode`) for the next steps.

**27.3 The admin route works (re-test defect 3)**
1. After the kiosk install, in the admin session (or as SYSTEM):
- [ ] `reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v ForceAutoLogon` finds no value;
      `AutoAdminLogon` = 1, `DefaultUserName` = ClubUser.
- [ ] `Get-ScheduledTask ClubKioskAutoLogon` is Ready; its principal is SYSTEM; its 3 actions run `reg.exe add`.
- [ ] `Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\ClubLauncher.lnk"` is False.
2. On the locked PC press Ctrl+Alt+Del > Switch user.
- [ ] The Windows sign-in screen stays (ClubUser does not come back by itself). Sign in as ClubAdmin.
- [ ] ClubAdmin gets the normal desktop: no lock screen, Alt+Tab and the Windows key work, Task Manager opens.
      `Get-Process Cms.Launcher | Select-Object Id, SessionId` shows only processes in the session of ClubUser.
- [ ] `(Start-Process C:\ClubLauncher\Cms.Launcher.exe -PassThru -Wait).ExitCode` is 4 (it ends at once for ClubAdmin);
      the same with `-ArgumentList '--watchdog'`.
- [ ] `reg query ... /v DefaultUserName` shows ClubUser (the task wrote it back after the sign-in of ClubAdmin).
3. Sign out of ClubAdmin (Start > ClubAdmin > Sign out).
- [ ] ClubUser signs in again within 30 s and its locked screen is back (`query user`: ClubUser Active). If the Windows
      sign-in screen stays instead, write it down (the documented way then is step 4).
4. Restart the PC.
- [ ] ClubUser signs in automatically and the lock screen starts.
5. Regression: Test 19 step 1 (staff "Log off") and 26.1 step 1 (`logoff <session id of ClubUser>`): within 10 s a new
   ClubUser session with the locked screen.
6. Player side: in the session of ClubUser press Ctrl+Alt+Del > Switch user.
- [ ] The Windows sign-in screen stays (known: ForceAutoLogon is off). The power icon > Restart brings ClubUser back.

**27.4 No stale message or PIN dialog (re-test defects 5 and 6)**
1. The PC is locked and has no session. Staff PIN unlock; the message "Готово / Розблоковано персоналом" opens. Do not
   press OK. Lock the PC in the admin UI.
- [ ] Within 3 s the message is gone. The player login form takes keys; Ctrl+Shift+U opens the PIN dialog.
2. Start a session and pause it. Press Ctrl+Shift+U (the PIN dialog opens). Resume in the admin UI; type nothing.
- [ ] Within 3 s the PIN dialog is gone and the session view is in front.
3. In a session of a player press "End session" (the question opens). Lock the PC in the admin UI.
- [ ] The question is gone (it counts as "No"): unlock the PC, the session goes on with its time.

**27.5 Texts (re-test LOW 7, 8, 9)**
1. Server update without `-EnrollmentToken` (25.4 server step).
- [ ] The end shows `-EnrollmentToken "<enrollment token>"` and "The enrollment token did not change. It is
      Devices:EnrollmentToken in C:\ClubServer\appsettings.json"; the token itself is not in the output.
2. Repeat 26.7 step 2 with the junction `C:\ProgramData\ClubAgent`.
- [ ] Exit code 1. The text says "A player may have made it" once, advises `cmd /c rmdir "C:\ProgramData\ClubAgent"`,
      has no `icacls ... /setowner`, and ends with "The old version (if any) runs as before. Only new, empty program
      folders or stricter permissions of C:\ClubAgent and C:\ClubLauncher can stay." The owner case of 26.7 step 2
      still advises `icacls "C:\ClubAgent" /setowner *S-1-5-32-544`.
3. The PC is locked, no session: staff PIN unlock, then lock it in the admin UI. Then a session: start, end.
- [ ] agent.log has "Reported the teardown at the end of a staff unlock without a session at ... (N program(s)
      closed)" for the first lock and "Reported the teardown of the ended session at ..." for the session.

## Known Issues / Debug
- If device doesn't appear: check `C:\ProgramData\ClubAgent\agent_heartbeat.txt` timestamp updates and
  `C:\ProgramData\ClubAgent\logs\agent.log`
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

