# Loyalty Tiers

Project authorship: created and maintained by Volodymyr Shabat.

This page tells how loyalty tiers work. A tier rewards the players who spend more at the club. The club sets the
tiers, for example Bronze, Silver and Gold. A tier can give a bonus on top-ups and a discount on PC time.

## How the server finds the tier of a player

1. The server adds up the real money that the player spent at the club in the last days of the period (for example
   30 days). The period moves every day.
2. The tier of the player is the tier with the highest "from spend" that this sum reaches.
3. If the sum is below the lowest tier, the player has no tier. Give the lowest tier the spend 0 to put every player in
   a tier.

Real money spent at the club:

| Counts | Does not count |
| --- | --- |
| PC time and packages paid from the real balance or at the desk | top-ups (they are deposits, not spend) |
| bar sales paid from the real balance or at the desk, minus bar refunds | the part paid with bonus |
| debt paid | debt that is not paid yet |
| kept reservation deposits and kept entry fees of events | free time |

Walk-in guests have no account, so they have no tier.

## What a tier gives

| Benefit | How it works |
| --- | --- |
| Bonus on top-ups | A top-up in cash or by card gets the bonus percent of the tier as bonus. It is a `bonus_accrual` ledger row of its own, next to the cashback of the amount. A refund of real balance takes back the cashback and the tier bonus of the refunded amount (at most the bonus that is left). |
| Discount on PC time | Tariff time (prepaid minutes, open-ended time, time from the balance), packages, extensions and the price difference of a move to another zone cost the discount percent less. |

Important rules:

- The session keeps the discount percent of its start. A tier change during the session does not change it.
- The discount does not apply to free time or to the fixed price of an event.
- The ledger row of the PC time holds the price that the player pays. Its description names the discount, for example
  "знижка Gold 10%: -4.50 ₴". So the balances, the Z-report and the analytics reconcile exactly as before.
- A session "from the balance" lasts longer for a player with a discount, because the server projects the end with the
  discounted price.
- The lock screen shows the list prices before the login. The discount applies when the player pays.

## Set the tiers

Only the Owner can change the tiers.

1. Open the tab "Тарифи / Tariffs" and find the card "Рівні лояльності / Loyalty tiers".
2. Set the period in days (7 to 365).
3. For each tier, type the name (1 to 20 characters), the spend from which it starts, the bonus on top-ups (0 to 50 %)
   and the discount on PC time (0 to 50 %). At most 6 tiers.
4. Click "Save". The audit log records `loyalty.settings_changed` with the old and the new tiers.

To turn loyalty off, delete all tiers and save.

The Manager and the Cashier see the tiers and how many players are in each tier, but cannot change them. The server
answers 403 to them (`PUT /api/loyalty` needs the Owner).

## Where staff see the tier

- The players list has the column "Рівень / Tier".
- The player card shows the tier, the money spent in the period, the bonus and the discount, and how much the player
  must still spend for the next tier.
- The audit row of a session start says "tier Gold -10%" when a discount applied. The audit row of a top-up says
  "tier Gold +25.00 ₴ bonus".

## API

| Request | Role | Result |
| --- | --- | --- |
| `GET /api/loyalty` | Owner, Manager, Cashier | the period, the tiers and the number of players in each tier |
| `PUT /api/loyalty` | Owner | saves the period and the tiers (400 `bad_loyalty_settings` for wrong values) |
| `GET /api/users/{id}/loyalty` | Owner, Manager, Cashier | the spend, the tier, the next tier and the amount still missing |
| `GET /api/users` | Owner, Manager, Cashier | each player with `tier` |

## Demo mode

The demo has three tiers for the last 30 days: Bronze from 0 ₴ (no benefit), Silver from 2000 ₴ (+2 % on top-ups,
-5 % on PC time) and Gold from 6000 ₴ (+5 %, -10 %). The demo players come to the club with different frequency, so
they spread over the three tiers.

## Limits

- The server calculates the spend when it needs it (top-up, session start, the players list). For a club with many
  years of history on SQLite, the players list reads the ledger rows of the players in memory.
- A tier is not stored. If you change the tiers, every player gets the new tier at once.
