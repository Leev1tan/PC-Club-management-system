using Cms.Server.Billing;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Shifts;

/// <summary>Money by payment method (bonus and debt are not money at the desk).</summary>
public sealed record MethodSums(decimal Cash, decimal Card, decimal Balance, decimal Bonus = 0, decimal Debt = 0)
{
    public decimal Total => Cash + Card + Balance + Bonus + Debt;
    public static readonly MethodSums Zero = new(0, 0, 0);
    public MethodSums Add(string method, decimal amount) => method switch
    {
        PaymentMethods.Cash => this with { Cash = Cash + amount },
        PaymentMethods.Card => this with { Card = Card + amount },
        PaymentMethods.Balance => this with { Balance = Balance + amount },
        PaymentMethods.Debt => this with { Debt = Debt + amount },
        _ => this
    };
}

public sealed record ZTime(decimal Total, MethodSums ByMethod, int Rows);
public sealed record ZBarItem(string Name, string Category, int Quantity, decimal Amount);
public sealed record ZBar(MethodSums Sales, MethodSums Refunds, decimal Net, int Receipts, int RefundReceipts, IReadOnlyList<ZBarItem> Items);
public sealed record ZCount(MethodSums ByMethod, int Count)
{
    public decimal Total => ByMethod.Total;
}
public sealed record ZBonus(decimal Issued, decimal Spent, decimal Reversed, decimal Adjusted);
public sealed record ZDebt(decimal Recorded, MethodSums Paid, decimal WrittenOff);
public sealed record ZPayout(long LedgerId, DateTimeOffset CreatedUtc, decimal Amount, string? Reason, string? By);
public sealed record ZPayouts(decimal Total, IReadOnlyList<ZPayout> Rows);
public sealed record ZAdjustments(decimal BalancePlus, decimal BalanceMinus);
/// <summary>Cash or card at the desk: money in, money out, net; for cash also the float and the expected cash.</summary>
public sealed record ZDesk(decimal In, decimal Out, decimal Net, decimal? Opening = null, decimal? Expected = null, decimal? Counted = null, decimal? Variance = null);
public sealed record ZStaff(string Username, int Rows, decimal CashNet, decimal CardNet, decimal TimeSales, decimal BarSales, decimal BarRefunds,
    decimal TopUps, decimal Refunds, decimal Payouts, decimal DebtRecorded);
/// <summary>Rows of other types (opening balances, legacy rows) with their desk money, so the sums always add up.</summary>
public sealed record ZOther(int Rows, decimal Cash, decimal Card);
/// <summary>Money held on player balances: reservation deposits (held, released, kept for a no-show) and prepaid bar orders.</summary>
public sealed record ZHolds(decimal DepositsHeld, decimal DepositsReleased, decimal DepositsKept, decimal OrdersHeld = 0, decimal OrdersReleased = 0)
{
    public static readonly ZHolds Zero = new(0, 0, 0);
    public static ZHolds Of(LedgerTotals t) => new(t.DepositsHeld, t.DepositsReleased, t.DepositsKept, t.OrdersHeld, t.OrdersReleased);
}
/// <summary>Reconciled: the cash and card of all sections equal the desk sums of the ledger rows of the shift.</summary>
public sealed record ZLedger(int Rows, long? FirstId, long? LastId, bool Reconciled);

/// <summary>
/// Z-report of a shift, computed from the ledger rows of the shift only (append-only, sums in
/// <see cref="LedgerTotals"/>, like the owner analytics), so it always reconciles with the ledger:
/// time revenue (sessions and packages), bar (sales, refunds, items),
/// top-ups, balance refunds, bonus issued / spent, debt, cash payouts, balance corrections, cash and
/// card at the desk with the expected and counted cash and the variance, and the same per staff member.
/// </summary>
public sealed record ZReport(
    ShiftView Shift, DateTimeOffset GeneratedUtc, string TimeZone,
    ZTime Time, ZBar Bar, ZCount TopUps, ZCount BalanceRefunds, ZBonus Bonus, ZDebt Debt, ZPayouts Payouts, ZAdjustments Adjustments,
    ZDesk Cash, ZDesk Card, ZOther Other, IReadOnlyList<ZStaff> ByStaff, ZLedger Ledger)
{
    /// <summary>Reservation deposits of the shift (a kept deposit is also in Time).</summary>
    public ZHolds Holds { get; init; } = ZHolds.Zero;

    public static async Task<ZReport> BuildAsync(CmsDbContext db, ShiftEntity shift, ClubClock clock, CancellationToken ct = default)
    {
        var rows = await db.Ledger.AsNoTracking().Where(l => l.ShiftId == shift.Id).OrderBy(l => l.Id).ToListAsync(ct);
        var sums = LedgerTotals.Of(rows);

        var expected = shift.OpeningFloat + sums.CashIn - sums.CashOut;
        var cash = new ZDesk(sums.CashIn, sums.CashOut, sums.CashNet, shift.OpeningFloat, expected, shift.CountedCash,
            shift.CountedCash.HasValue ? shift.CountedCash.Value - expected : null);
        var card = new ZDesk(sums.CardIn, sums.CardOut, sums.CardNet);

        var saleIds = await db.Sales.AsNoTracking().Where(s => s.ShiftId == shift.Id).Select(s => new { s.Id, s.Kind }).ToListAsync(ct);
        var ids = saleIds.Select(s => s.Id).ToList();
        var kinds = saleIds.ToDictionary(s => s.Id, s => s.Kind);
        var items = (await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).ToListAsync(ct))
            .GroupBy(i => (i.Name, i.Category))
            .Select(g => new ZBarItem(g.Key.Name, g.Key.Category,
                g.Sum(i => kinds[i.SaleId] == SaleKinds.Refund ? -i.Quantity : i.Quantity),
                g.Sum(i => kinds[i.SaleId] == SaleKinds.Refund ? -i.LineTotal : i.LineTotal)))
            .Where(i => i.Quantity != 0 || i.Amount != 0)
            .OrderByDescending(i => i.Amount).ThenBy(i => i.Name).ToList();

        var byStaff = rows.GroupBy(r => r.ActorUsername ?? "—").Select(g => new ZStaff(
            g.Key, g.Count(),
            g.Where(r => r.Method == PaymentMethods.Cash).Sum(r => r.DeskAmount),
            g.Where(r => r.Method == PaymentMethods.Card).Sum(r => r.DeskAmount),
            g.Where(r => r.Type is LedgerTypes.SessionCharge or LedgerTypes.PackagePurchase).Sum(r => r.Amount),
            g.Where(r => r.Type == LedgerTypes.BarSale).Sum(r => r.Amount),
            g.Where(r => r.Type == LedgerTypes.BarRefund).Sum(r => r.Amount),
            g.Where(r => r.Type == LedgerTypes.TopUp).Sum(r => r.Amount),
            g.Where(r => r.Type == LedgerTypes.Refund).Sum(r => r.Amount),
            g.Where(r => r.Type == LedgerTypes.CashPayout).Sum(r => r.Amount),
            g.Where(r => r.Method == PaymentMethods.Debt).Sum(r => r.DebtDelta)))
            .OrderBy(s => s.Username).ToList();

        return new ZReport(
            ShiftView.From(shift), clock.UtcNow, clock.Zone.Id,
            new ZTime(sums.TimeTotal, sums.Time, sums.TimeRows),
            new ZBar(sums.BarSales, sums.BarRefunds, sums.BarNet, sums.Receipts, sums.RefundReceipts, items),
            new ZCount(sums.TopUps, sums.TopUpCount), new ZCount(sums.Refunds, sums.RefundCount),
            new ZBonus(sums.BonusIssued, sums.BonusSpent, sums.BonusReversed, sums.BonusAdjusted),
            new ZDebt(sums.DebtRecorded, sums.DebtPaid, sums.DebtWrittenOff),
            new ZPayouts(sums.Payouts, sums.PayoutRows.ToList()),
            new ZAdjustments(sums.BalancePlus, sums.BalanceMinus),
            cash, card, new ZOther(sums.OtherRows, sums.OtherCash, sums.OtherCard), byStaff,
            new ZLedger(rows.Count, rows.FirstOrDefault()?.Id, rows.LastOrDefault()?.Id, sums.Reconciled))
        {
            Holds = ZHolds.Of(sums)
        };
    }
}
