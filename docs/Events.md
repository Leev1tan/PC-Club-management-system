# Events and Tournaments

Project authorship: created and maintained by Volodymyr Shabat.

This page tells how a club runs an event or a tournament with the system (spec area 9). An event uses a set of PCs for
a fixed time. Players or teams register before the event. At the start, the PCs of the players unlock together, and
all their sessions end at the same time.

## What an event is

| Part | Meaning |
| --- | --- |
| PCs | All approved PCs of a zone, or PCs that you choose. The event books these PCs for its time. |
| Time | A start (club time) and a duration from 30 minutes to 12 hours, in steps of 15 minutes. |
| Teams | Teams of 1 to 10 players. A team of 1 is a single player. Each team has a captain and a name. |
| PC time | Free, or a fixed price per player. |
| Entry fee | Optional. Each player pays it from the real balance at the registration. |
| Bracket | A single-elimination bracket. Staff enter the results. |

Only registered players can take part. A walk-in guest must get a player account first, because the entry fee and the
session need an account.

## Who can do what

| Action | Owner | Manager | Cashier | Tech |
| --- | --- | --- | --- | --- |
| See the events | yes | yes | yes | no |
| Create, move, start, finish or cancel an event; make the bracket | yes | yes | no | no |
| Register or withdraw a team; start the session of one player again | yes | yes | yes | no |
| Enter or clear a match result | yes | yes | yes | no |

The server checks every request. A request without a login gets 401. A cashier gets 403 for the manager actions.

## Create an event

1. Open the tab "Події / Events" and click "+ New event".
2. Type the name. The lock screens show this name.
3. Choose the date, the start and the duration.
4. Choose the PCs: the whole zone, or single PCs.
5. Choose the number of players per team.
6. Choose the PC time: free, or a fixed price per player.
7. Type the entry fee per player (0 = no fee).

The server refuses the event when one of its PCs has a reservation or another event at that time. It also refuses the
event when a zone then has too few PCs for its "any PC" reservations. After you create the event, a reservation on its
PCs is refused for the time of the event, and the public booking page shows fewer free PCs.

You can move a scheduled event (name, start, duration). The PCs stay the same.

## Register teams

1. Open the event and click "Register a team" (or "Register a player").
2. Choose the players and the captain. Type the team name (not needed for a single player).
3. Click "Register".

The server gives each player a PC of the event: the first free PCs in name order, so a team sits together. The number
of players can never be more than the number of PCs. Two cashiers who register at the same moment cannot overbook the
event, and one player cannot be in two teams of the same event.

If the event has an entry fee, the server takes it from the real balance of each player. Bonus never pays an entry fee.
If one player of the team has too little real balance, nothing changes and the error names that player.

A team can withdraw before the start. Its players get the entry fee back on the real balance, and its PCs are free
again.

## Start, end and the lock screen

- From 15 minutes before the start (the hold time of the reservation settings), the PCs of the event are held. The lock
  screen shows "EVENT «name» AT 18:00". A walk-in session or a player login on such a PC is refused. The owner or a
  manager can override this hold for one session; the audit log records `event.hold_overridden`.
- At the start time a background job starts the event. A manager can also click "Start now" earlier.
- At the start, each player with a PC gets a session that ends at the end of the event (a common timer). The PC
  unlocks. The session panel shows "🏆 Event: name" and the time left.
- If the PC time is free, the session is free time. The audit log records `session.free_time` with the reason
  "event #N".
- If the PC time has a fixed price, the player pays it at the start from the balances (bonus first). The loyalty
  discount does not apply to the price of an event.
- A player without a session (too little balance, the PC is busy, or the player plays on another PC) keeps an error on
  the seat. Fix the cause (for example, top up the balance) and click "Start" on that seat.
- A PC of the event without a player stays locked. While the event runs, its lock screen shows the time left of the
  event.
- At the end, the sessions end and the PCs lock. The event status becomes "Finished".
- A manager can finish the event early. Then the sessions end at once. Unused prepaid time is not refunded.

## Cancel an event

A manager can cancel a scheduled or a running event, with an optional reason. The held entry fees go back to the real
balances of the players. The sessions of a running event end at once.

If the server was off for the whole time of a scheduled event, the background job cancels the event after its end and
returns the entry fees.

## Bracket

1. When at least two teams are registered, a manager clicks "Bracket by registration" or "Bracket by draw".
2. The size of the bracket is the next power of two. The best seeds get the byes (a team without an opponent goes to
   the next round).
3. While the event runs (or after it finished), staff click a match and choose the winner. The score is optional.
4. The winner goes to the next round. The winner of the final wins the event.

You can change or clear a result while the next match has no result. A new registration or a withdrawal deletes a
bracket that has no results yet. A bracket with results cannot be made again.

## Money and reports

| Ledger row | When | Balance | Revenue |
| --- | --- | --- | --- |
| `event_fee_hold` | registration | − entry fee (real balance) | no |
| `event_fee_release` | withdrawal or cancel | + entry fee | no |
| `event_fee_kept` | the event is over | no change | yes, like PC time paid from the balance |
| `session_charge` | start, fixed price | − price (bonus first) | yes, PC time |

- The Z-report, the analytics and both CSV files show the entry fees held, returned and kept.
- `GET /api/ledger/holds` (Owner, Manager) checks every entry fee against its ledger rows (`eventFees`).
- The balances always equal the sum of the ledger rows (`GET /api/ledger/reconcile`).

## Audit log

The audit log records every change: `event.created`, `event.updated`, `event.team_registered`, `event.team_withdrawn`,
`event.started`, `event.seat_started`, `event.bracket_created`, `event.result`, `event.finished`, `event.cancelled` and
`event.hold_overridden`. The rows contain the event id, the team ids, the nicknames of the players, the PCs, the times
and the amounts. They never contain the name of an event or a team, because staff type these names and the audit log
cannot be erased.

## Demo mode

The demo has two events:

- a finished tournament three days ago: 4 teams of 2 on STD-01 to STD-08, free PC time, an entry fee of 50 ₴ per
  player (kept), a bracket with results;
- an upcoming tournament in two days on the VIP PCs: teams of 2, PC time 60 ₴ per player, an entry fee of 100 ₴; two
  teams are registered and one place is free.

## Limits

- Seats are given in registration order. You cannot move a player to another PC of the event.
- A team must have exactly the number of players of the event.
- The lock screen shows the event only after the agent got the next heartbeat (about 5 seconds).
