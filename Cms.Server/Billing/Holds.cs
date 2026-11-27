using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>Held money: the total that is held now, the number of open holds, and the ids whose ledger rows do not match.</summary>
public sealed record HoldCheck(decimal Held, int Open, bool Ok, IReadOnlyList<long> Broken);
public sealed record HoldsView(HoldCheck Deposits);

/// <summary>
/// Reconciliation of the money that the ledger holds for the club on player balances. Reservation deposits: per
/// reservation the rows deposit_hold - deposit_release - deposit_kept equal the deposit while it is held and 0
/// after it was applied, returned or kept; every deposit row points to a reservation with that deposit.
/// </summary>
public static class Holds
{
    public static async Task<HoldsView> CheckAsync(CmsDbContext db, CancellationToken ct = default) => new(await DepositsAsync(db, ct));

    public static async Task<HoldCheck> DepositsAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var types = new[] { LedgerTypes.DepositHold, LedgerTypes.DepositRelease, LedgerTypes.DepositKept };
        var rows = await db.Ledger.AsNoTracking().Where(l => types.Contains(l.Type))
            .Select(l => new { l.ReservationId, l.Type, l.Amount, l.BalanceDelta }).ToListAsync(ct);
        var reservations = await db.Reservations.AsNoTracking().Where(r => r.DepositStatus != DepositStatuses.None)
            .Select(r => new { r.Id, r.Deposit, r.DepositStatus }).ToListAsync(ct);
        var broken = new SortedSet<long>();
        foreach (var orphan in rows.Where(r => r.ReservationId == null || reservations.All(x => x.Id != r.ReservationId)))
            broken.Add(orphan.ReservationId ?? 0);
        decimal held = 0;
        var open = 0;
        foreach (var r in reservations)
        {
            var mine = rows.Where(x => x.ReservationId == r.Id).ToList();
            decimal Sum(string type) => mine.Where(x => x.Type == type).Sum(x => x.Amount);
            var hold = Sum(LedgerTypes.DepositHold);
            var net = hold - Sum(LedgerTypes.DepositRelease) - Sum(LedgerTypes.DepositKept);
            var balance = mine.Sum(x => x.BalanceDelta);
            var expected = r.DepositStatus == DepositStatuses.Held ? r.Deposit : 0m;
            var ok = hold == r.Deposit && net == expected &&
                     balance == -(r.DepositStatus is DepositStatuses.Held or DepositStatuses.Kept ? r.Deposit : 0m) &&
                     (r.DepositStatus != DepositStatuses.Kept || Sum(LedgerTypes.DepositKept) == r.Deposit);
            if (!ok) broken.Add(r.Id);
            if (r.DepositStatus == DepositStatuses.Held)
            {
                held += r.Deposit;
                open++;
            }
        }
        return new HoldCheck(held, open, broken.Count == 0, broken.ToList());
    }
}
