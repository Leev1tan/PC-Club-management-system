# Club Game-Account Pool

Project authorship: created and maintained by Volodymyr Shabat.

This page tells how the club account pool works and what its security limits are. Read the whole page before you put a
real account into the pool.

## What the pool does

Some games need an account of a platform (Steam, Epic Games, Battle.net and others). A club can own accounts that have
the game and lend them to the players for one session.

1. Staff mark a game of the catalog with the platform of the club account (tab "Ігри / Games", field "Club account for
   the game").
2. During a session, the player clicks the tile of that game on the lock screen.
3. The lock screen asks the agent of the PC. The agent asks the server for a lease.
4. The server gives the PC a free account of that platform for this session (a lease).
5. The agent fetches the login and the secret of the lease, one time only, and gives them to the lock screen.
6. The lock screen starts the client of the platform with the login arguments of this PC, then it starts the game.
7. The lease ends when the session ends, when the player logs out, when the session moves to another PC, when staff
   free the account, or when the server finds that the session is gone (every 5 seconds and at the start of the server).

## Before you use shared accounts

- **Read the terms of each platform.** Many platforms do not allow the sharing of an account between many people, or
  allow it only in a special program for internet cafes (for example the cybercafe program of Valve for Steam). The
  club alone is responsible for this check. Do not use the pool when the terms of a platform do not allow it.
- **Two-step verification.** An account with two-step verification (for example Steam Guard, an e-mail code or an
  authenticator app) cannot log in automatically, because the client asks for a code. The system does not bypass
  two-step verification or any other protection of a platform, and it never will. Use only accounts that the terms of
  the platform allow to log in with a login and a password.
- **One account, one PC.** The server never gives one account to two PCs at the same time, and never gives two
  accounts of one platform to one PC.

## Security design

| Point | What the system does |
| --- | --- |
| Storage | The login and the secret are encrypted with ASP.NET Core Data Protection, with a purpose of their own (`Cms.GameAccounts.Credentials.v1`). The database holds only the encrypted values and a masked login ("de•••01"). |
| Keys | Data Protection uses the key ring of the server in the data directory (`keys`), the same as the login cookies. Back up the key ring together with the database. Without the key ring the server cannot read the secrets; then set a new secret for each account. |
| Staff API | No answer ever contains the login or the secret. Staff see the platform, the masked login, "secret stored: yes / no", on or off, and which PC uses the account. The secret is write-only. |
| Roles | Only the Owner or a Manager can add an account, set a new secret, switch it off or remove it. A Cashier sees which PC uses which account (masked login) and can free a stuck account. The server answers 401 without a login and 403 for a Cashier. |
| Delivery | Only the agent of the PC that has the lease can fetch the credentials, with its device key, while the session of the lease runs on that PC. The server gives them one time per lease; a second request gets 409 `credential_delivered`. The answer has `Cache-Control: no-store`. |
| Lock screen | The agent gives the credentials only to the installed lock screen: it checks that the program of the pipe client is `AccountPool:LauncherPath` (default `C:\ClubLauncher\Cms.Launcher.exe`). Any other program gets 403 `pipe_client_untrusted`. The lock screen never shows the secret. |
| Audit log | `game_account.added`, `.secret_rotated`, `.updated`, `.removed`, `.released`, `.leased` and `.delivered`, with the platform and the masked login only. The audit log, the server log, the error answers and the CSV exports never contain a login or a secret. |
| Removal | A removed account loses its encrypted login and secret at once. The row stays for the history of the leases. |

## Limits (read before you go live)

- **A program that runs as the player can in principle read the secret.** The lock screen runs as the Windows user of
  the player, and it gives the login and the secret to the client of the platform as process arguments. Another program
  of the same Windows user can read the arguments of that process, or read the memory of the lock screen. The kiosk
  setup (players are standard users, the lock screen is the shell, only catalog games start) makes this harder, but it
  does not make it impossible. Use the pool only for accounts that you can afford to lose, and set a new secret often.
- **The network.** The server sends the credentials to the agent over the club LAN. Without HTTPS they cross the LAN in
  clear text, like the device keys. Keep the club PCs in a network that players cannot sniff (switch port isolation),
  or put an HTTPS reverse proxy in front of the server (see [ClubDeployment.md](ClubDeployment.md)).
- **The client may remember the login.** Some clients keep a remembered login in their own folder (for Steam, in the
  config folder of the Steam installation folder). The session teardown closes the clients and deletes only the
  configured paths inside the allowed roots (`%LOCALAPPDATA%`, `%APPDATA%`). Check on one PC that the next player does
  not inherit the login, and turn off "remember me" options of the client where it has them.
- **The arguments of a client change.** Whether a client accepts a login and a password as arguments depends on the
  client and its version. Check it on one PC before you use the pool. If a client does not accept them, do not use the
  pool for that platform.
- **No Windows test here.** The agent and the lock screen code were built for Windows and their logic is tested on
  Linux; the start of a real client was not tested on a Windows PC (see Windows test 23).

## Set up a PC

The settings of the client start are on each PC, in `C:\ClubAgent\appsettings.json` (administrators only; the server
never sends them, so nobody can change the program or its arguments over the network):

```json
"AccountPool": {
  "LauncherPath": "C:\\ClubLauncher\\Cms.Launcher.exe",
  "Platforms": {
    "steam": {
      "ClientPath": "C:\\Program Files (x86)\\Steam\\steam.exe",
      "LoginArguments": "-login {login} {secret}"
    }
  }
}
```

- `ClientPath` must be an absolute path to an `.exe` file (`C:\...` or `%ProgramFiles%\...`).
- `LoginArguments` must contain `{login}`; `{secret}` is optional. Each part between spaces becomes one argument, so a
  login or a secret with spaces or quotes stays one argument.
- Restart the agent service after a change. The agent writes the usable platforms into
  `C:\ProgramData\ClubAgent\account-pool.json` for the lock screen and logs a warning for a platform with wrong values.
- A PC without the platform shows "This PC is not set up to log in to Steam with a club account" and does not ask for a
  lease.

## Add accounts

1. Open the tab "Акаунти ігор / Game accounts" (Owner or Manager) and click "+ New account".
2. Choose the platform, type the login and the secret. You can add the account without a secret and set it later; an
   account without a secret is never leased.
3. Click "Add". The login of a platform must be unique.

To change the password of an account, change it at the platform first, then click "New secret" and type the new one. A
PC that uses the account keeps its lease. "Turn off" stops new leases of the account. "Delete" removes the account and
ends its lease.

## Free a stuck account

If a PC keeps an account (for example the PC lost power), a Cashier clicks "Free it" on the row of the account. The
lease ends; the client on that PC can stay logged in until its session ends and the teardown closes it.

## API

| Request | Who | Result |
| --- | --- | --- |
| `GET /api/game-accounts` | Owner, Manager, Cashier | the accounts with the masked login, `hasSecret` and the current lease |
| `POST /api/game-accounts` | Owner, Manager | add (`platform`, `login`, optional `secret`) |
| `PUT /api/game-accounts/{id}/secret` | Owner, Manager | a new secret |
| `PUT /api/game-accounts/{id}/active` | Owner, Manager | on or off |
| `DELETE /api/game-accounts/{id}` | Owner, Manager | remove |
| `POST /api/game-accounts/{id}/release` | Owner, Manager, Cashier | free the account |
| `POST /api/devices/{id}/game-account` | the agent of the PC (device key) | a lease for `gameId`: lease id, platform, masked login, delivered |
| `POST /api/devices/{id}/game-account/{leaseId}/credential` | the agent of the PC (device key) | the login and the secret, once |

## Demo mode

The demo has only fake accounts: `demo-steam-01` to `demo-steam-06` and `demo-epic-01`, `demo-epic-02` (the second one
without a secret). The demo makes new random secrets at every reset. These accounts exist at no platform. Three demo
games need a Steam account (Grand Theft Auto V, Rust, EA SPORTS FC 25); when a simulated player starts one of them, the
simulated agent leases an account and fetches the fake credentials once.
