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
/// <summary>Reconciled: the cash and card of all sections equal the desk sums of the ledger rows of the shift.</summary>
public sealed record ZLedger(int Rows, long? FirstId, long? LastId, bool Reconciled);

/// <summary>
/// Z-report of a shift, computed from the ledger rows of the shift only (append-only), so it always
/// reconciles with the ledger: time revenue (sessions and packages), bar (sales, refunds, items),
/// top-ups, balance refunds, bonus issued / spent, debt, cash payouts, balance corrections, cash and
/// card at the desk with the expected and counted cash and the variance, and the same per staff member.
/// </summary>
public sealed record ZReport(
    ShiftView Shift, DateTimeOffset GeneratedUtc, string TimeZone,
    ZTime Time, ZBar Bar, ZCount TopUps, ZCount BalanceRefunds, ZBonus Bonus, ZDebt Debt, ZPayouts Payouts, ZAdjustments Adjustments,
    ZDesk Cash, ZDesk Card, ZOther Other, IReadOnlyList<ZStaff> ByStaff, ZLedger Ledger)
{
    public static async Task<ZReport> BuildAsync(CmsDbContext db, ShiftEntity shift, ClubClock clock, CancellationToken ct = default)
    {
        var rows = await db.Ledger.AsNoTracking().Where(l => l.ShiftId == shift.Id).OrderBy(l => l.Id).ToListAsync(ct);

        var time = MethodSums.Zero;
        decimal timeTotal = 0;
        int timeRows = 0;
        var barSales = MethodSums.Zero;
        var barRefunds = MethodSums.Zero;
        int receipts = 0, refundReceipts = 0;
        var topUps = MethodSums.Zero;
        var refunds = MethodSums.Zero;
        int topUpCount = 0, refundCount = 0;
        decimal bonusIssued = 0, bonusSpent = 0, bonusReversed = 0, bonusAdjusted = 0;
        decimal debtRecorded = 0, debtWrittenOff = 0;
        var debtPaid = MethodSums.Zero;
        var payouts = new List<ZPayout>();
        decimal balancePlus = 0, balanceMinus = 0;
        int otherRows = 0;
        decimal otherCash = 0, otherCard = 0;

        foreach (var r in rows)
        {
            var desk = PaymentMethods.IsDesk(r.Method);
            switch (r.Type)
            {
                case LedgerTypes.SessionCharge or LedgerTypes.PackagePurchase:
                    timeRows++;
                    timeTotal += r.Amount;
                    time = desk ? time.Add(r.Method, r.DeskAmount)
                        : r.Method == PaymentMethods.Debt ? time.Add(PaymentMethods.Debt, r.DebtDelta)
                        : time with { Balance = time.Balance - r.BalanceDelta, Bonus = time.Bonus - r.BonusDelta };
                    bonusSpent -= r.BonusDelta;
                    if (r.Method == PaymentMethods.Debt) debtRecorded += r.DebtDelta;
                    break;
                case LedgerTypes.BarSale:
                    receipts++;
                    barSales = barSales.Add(r.Method, desk ? r.DeskAmount : -r.BalanceDelta);
                    break;
                case LedgerTypes.BarRefund:
                    refundReceipts++;
                    barRefunds = barRefunds.Add(r.Method, desk ? -r.DeskAmount : r.BalanceDelta);
                    break;
                case LedgerTypes.TopUp:
                    topUpCount++;
                    topUps = topUps.Add(r.Method, r.DeskAmount);
                    break;
                case LedgerTypes.Refund:
                    refundCount++;
                    refunds = refunds.Add(r.Method, -r.DeskAmount);
                    break;
                case LedgerTypes.BonusAccrual:
                    bonusIssued += r.BonusDelta;
                    break;
                case LedgerTypes.BonusReversal:
                    bonusReversed -= r.BonusDelta;
                    break;
                case LedgerTypes.Adjustment:
                    bonusAdjusted += r.BonusDelta;
                    if (r.BalanceDelta > 0) balancePlus += r.BalanceDelta; else balanceMinus -= r.BalanceDelta;
                    break;
                case LedgerTypes.DebtPayment:
                    debtPaid = debtPaid.Add(r.Method, desk ? r.DeskAmount : -r.BalanceDelta);
                    break;
                case LedgerTypes.DebtWriteOff:
                    debtWrittenOff -= r.DebtDelta;
                    break;
                case LedgerTypes.CashPayout:
                    payouts.Add(new ZPayout(r.Id, r.CreatedUtc, -r.DeskAmount, r.Reason, r.ActorUsername));
                    break;
                default:
                    otherRows++;
                    if (r.Method == PaymentMethods.Cash) otherCash += r.DeskAmount;
                    if (r.Method == PaymentMethods.Card) otherCard += r.DeskAmount;
                    break;
            }
        }

        decimal Desk(string method, bool positive) => rows.Where(r => r.Method == method && (positive ? r.DeskAmount > 0 : r.DeskAmount < 0)).Sum(r => r.DeskAmount);
        var cashIn = Desk(PaymentMethods.Cash, true);
        var cashOut = -Desk(PaymentMethods.Cash, false);
        var cardIn = Desk(PaymentMethods.Card, true);
        var cardOut = -Desk(PaymentMethods.Card, false);
        var payoutTotal = payouts.Sum(p => p.Amount);
        var expected = shift.OpeningFloat + cashIn - cashOut;
        var cash = new ZDesk(cashIn, cashOut, cashIn - cashOut, shift.OpeningFloat, expected, shift.CountedCash,
            shift.CountedCash.HasValue ? shift.CountedCash.Value - expected : null);
        var card = new ZDesk(cardIn, cardOut, cardIn - cardOut);

        // Every desk amount belongs to exactly one section
        var cashSections = time.Cash + barSales.Cash - barRefunds.Cash + topUps.Cash - refunds.Cash + debtPaid.Cash - payoutTotal + otherCash;
        var cardSections = time.Card + barSales.Card - barRefunds.Card + topUps.Card - refunds.Card + debtPaid.Card + otherCard;
        var reconciled = cashSections == cash.Net && cardSections == card.Net;

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
            new ZTime(timeTotal, time, timeRows),
            new ZBar(barSales, barRefunds, barSales.Total - barRefunds.Total, receipts, refundReceipts, items),
            new ZCount(topUps, topUpCount), new ZCount(refunds, refundCount),
            new ZBonus(bonusIssued, bonusSpent, bonusReversed, bonusAdjusted),
            new ZDebt(debtRecorded, debtPaid, debtWrittenOff),
            new ZPayouts(payoutTotal, payouts),
            new ZAdjustments(balancePlus, balanceMinus),
            cash, card, new ZOther(otherRows, otherCash, otherCard), byStaff,
            new ZLedger(rows.Count, rows.FirstOrDefault()?.Id, rows.LastOrDefault()?.Id, reconciled));
    }
}
