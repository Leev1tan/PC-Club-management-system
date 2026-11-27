using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>Held money: the total that is held now, the number of open holds, and the ids whose ledger rows do not match.</summary>
public sealed record HoldCheck(decimal Held, int Open, bool Ok, IReadOnlyList<long> Broken);
public sealed record HoldsView(HoldCheck Deposits, HoldCheck Orders)
{
    /// <summary>Entry fees of events held on the balances (Events/EventService).</summary>
    public HoldCheck EventFees { get; init; } = new(0m, 0, true, Array.Empty<long>());
}

/// <summary>
/// Reconciliation of the money that the ledger holds for the club on player balances. Reservation deposits: per
/// reservation the rows deposit_hold - deposit_release - deposit_kept equal the deposit while it is held and 0
/// after it was applied, returned or kept; every deposit row points to a reservation with that deposit. Prepaid bar
/// orders: order_hold - order_release equals the total while the order is open and 0 after the delivery or a cancel.
/// Entry fees of events: per player of an event the rows event_fee_hold - event_fee_release - event_fee_kept equal the
/// fee while it is held and 0 after it was returned or kept (ids in Broken are event ids).
/// </summary>
public static class Holds
{
    public static async Task<HoldsView> CheckAsync(CmsDbContext db, CancellationToken ct = default) =>
        new(await DepositsAsync(db, ct), await OrdersAsync(db, ct)) { EventFees = await EventFeesAsync(db, ct) };

    public static async Task<HoldCheck> EventFeesAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var types = new[] { LedgerTypes.EventFeeHold, LedgerTypes.EventFeeRelease, LedgerTypes.EventFeeKept };
        var rows = await db.Ledger.AsNoTracking().Where(l => types.Contains(l.Type))
            .Select(l => new { l.EventId, l.UserId, l.Type, l.Amount, l.BalanceDelta }).ToListAsync(ct);
        var players = await db.EventPlayers.AsNoTracking().Where(p => p.FeeStatus != EventFeeStatuses.None)
            .Select(p => new { p.EventId, p.UserId, p.Fee, p.FeeStatus }).ToListAsync(ct);
        var broken = new SortedSet<long>();
        foreach (var orphan in rows.Where(r => r.EventId == null || !players.Any(p => p.EventId == r.EventId && p.UserId == r.UserId)))
            broken.Add(orphan.EventId ?? 0);
        decimal held = 0;
        var open = 0;
        // A player may register again after the team withdrew: the rows of one player add up over all registrations
        foreach (var g in players.GroupBy(p => (p.EventId, p.UserId)))
        {
            var mine = rows.Where(r => r.EventId == g.Key.EventId && r.UserId == g.Key.UserId).ToList();
            decimal Sum(string type) => mine.Where(x => x.Type == type).Sum(x => x.Amount);
            var expectedHeld = g.Where(p => p.FeeStatus == EventFeeStatuses.Held).Sum(p => p.Fee);
            var expectedKept = g.Where(p => p.FeeStatus == EventFeeStatuses.Kept).Sum(p => p.Fee);
            var ok = Sum(LedgerTypes.EventFeeHold) == g.Sum(p => p.Fee) &&
                     Sum(LedgerTypes.EventFeeHold) - Sum(LedgerTypes.EventFeeRelease) - Sum(LedgerTypes.EventFeeKept) == expectedHeld &&
                     Sum(LedgerTypes.EventFeeKept) == expectedKept &&
                     mine.Sum(x => x.BalanceDelta) == -(expectedHeld + expectedKept);
            if (!ok) broken.Add(g.Key.EventId);
            held += expectedHeld;
            open += g.Count(p => p.FeeStatus == EventFeeStatuses.Held);
        }
        return new HoldCheck(held, open, broken.Count == 0, broken.ToList());
    }

    public static async Task<HoldCheck> OrdersAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var rows = await db.Ledger.AsNoTracking().Where(l => l.Type == LedgerTypes.OrderHold || l.Type == LedgerTypes.OrderRelease)
            .Select(l => new { l.BarOrderId, l.Type, l.Amount, l.BalanceDelta }).ToListAsync(ct);
        var orders = await db.BarOrders.AsNoTracking().Where(o => o.Payment == BarOrderPayments.Balance)
            .Select(o => new { o.Id, o.Total, o.Status }).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToHashSet();
        var broken = new SortedSet<long>(rows.Where(r => r.BarOrderId is not { } id || !ids.Contains(id)).Select(r => r.BarOrderId ?? 0));
        decimal held = 0;
        var open = 0;
        foreach (var o in orders)
        {
            var mine = rows.Where(r => r.BarOrderId == o.Id).ToList();
            var hold = mine.Where(r => r.Type == LedgerTypes.OrderHold).Sum(r => r.Amount);
            var release = mine.Where(r => r.Type == LedgerTypes.OrderRelease).Sum(r => r.Amount);
            var isOpen = o.Status is BarOrderStatuses.New or BarOrderStatuses.Preparing;
            if (hold != o.Total || hold - release != (isOpen ? o.Total : 0m) || mine.Sum(r => r.BalanceDelta) != (isOpen ? -o.Total : 0m)) broken.Add(o.Id);
            if (isOpen)
            {
                held += o.Total;
                open++;
            }
        }
        return new HoldCheck(held, open, broken.Count == 0, broken.ToList());
    }

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
