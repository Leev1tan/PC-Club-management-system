using Cms.Server.Billing;
using Cms.Server.Data;

namespace Cms.Server.Shifts;

/// <summary>
/// Sums of ledger rows by section. The Z-report (the rows of one shift) and the owner analytics (the
/// rows of a period) both use it, so their numbers follow the same rules and reconcile:
///  - PC time (sessions and packages) by how it was paid: cash, card, balance, bonus, debt;
///  - bar sales and refunds by method, receipts; top-ups and balance refunds; bonus issued and spent;
///    debt recorded, paid and written off; cash payouts; balance corrections; other rows;
///  - cash and card at the desk (in, out, net). Every desk amount is in exactly one section, so the
///    sections add up to the desk money of the rows (<see cref="Reconciled"/>).
/// </summary>
public sealed class LedgerTotals
{
    private readonly HashSet<Guid> _sessions = new();
    private readonly List<ZPayout> _payouts = new();

    public MethodSums Time { get; private set; } = MethodSums.Zero;
    /// <summary>Value of the PC time sold (amount of the rows), whatever paid for it.</summary>
    public decimal TimeTotal { get; private set; }
    public int TimeRows { get; private set; }
    public MethodSums BarSales { get; private set; } = MethodSums.Zero;
    public MethodSums BarRefunds { get; private set; } = MethodSums.Zero;
    public int Receipts { get; private set; }
    public int RefundReceipts { get; private set; }
    public MethodSums TopUps { get; private set; } = MethodSums.Zero;
    public int TopUpCount { get; private set; }
    public MethodSums Refunds { get; private set; } = MethodSums.Zero;
    public int RefundCount { get; private set; }
    public decimal BonusIssued { get; private set; }
    public decimal BonusSpent { get; private set; }
    public decimal BonusReversed { get; private set; }
    public decimal BonusAdjusted { get; private set; }
    public decimal DebtRecorded { get; private set; }
    public MethodSums DebtPaid { get; private set; } = MethodSums.Zero;
    public decimal DebtWrittenOff { get; private set; }
    public decimal BalancePlus { get; private set; }
    public decimal BalanceMinus { get; private set; }
    public int OtherRows { get; private set; }
    public decimal OtherCash { get; private set; }
    public decimal OtherCard { get; private set; }
    public decimal CashIn { get; private set; }
    public decimal CashOut { get; private set; }
    public decimal CardIn { get; private set; }
    public decimal CardOut { get; private set; }
    public int Rows { get; private set; }
    public long? FirstId { get; private set; }
    public long? LastId { get; private set; }

    public IReadOnlyList<ZPayout> PayoutRows => _payouts;
    public decimal Payouts => _payouts.Sum(p => p.Amount);
    /// <summary>Sessions with PC time rows (for the average ticket).</summary>
    public int Sessions => _sessions.Count;
    public decimal BarNet => BarSales.Total - BarRefunds.Total;
    public decimal CashNet => CashIn - CashOut;
    public decimal CardNet => CardIn - CardOut;

    /// <summary>Revenue = PC time + bar (net), by how it was paid (bonus and debt only for PC time).</summary>
    public MethodSums Revenue => new(
        Time.Cash + BarSales.Cash - BarRefunds.Cash,
        Time.Card + BarSales.Card - BarRefunds.Card,
        Time.Balance + BarSales.Balance - BarRefunds.Balance,
        Time.Bonus, Time.Debt);

    public decimal RevenueTotal => TimeTotal + BarNet;

    /// <summary>The cash and card of all sections equal the desk sums of the rows.</summary>
    public bool Reconciled
    {
        get
        {
            var cash = Time.Cash + BarSales.Cash - BarRefunds.Cash + TopUps.Cash - Refunds.Cash + DebtPaid.Cash - Payouts + OtherCash;
            var card = Time.Card + BarSales.Card - BarRefunds.Card + TopUps.Card - Refunds.Card + DebtPaid.Card + OtherCard;
            return cash == CashNet && card == CardNet;
        }
    }

    public static LedgerTotals Of(IEnumerable<LedgerEntryEntity> rows)
    {
        var totals = new LedgerTotals();
        foreach (var row in rows) totals.Add(row);
        return totals;
    }

    public void Add(LedgerEntryEntity r)
    {
        Rows++;
        FirstId = FirstId is { } first && first < r.Id ? first : r.Id;
        LastId = LastId is { } last && last > r.Id ? last : r.Id;
        if (r.Method == PaymentMethods.Cash)
        {
            if (r.DeskAmount > 0) CashIn += r.DeskAmount; else CashOut -= r.DeskAmount;
        }
        else if (r.Method == PaymentMethods.Card)
        {
            if (r.DeskAmount > 0) CardIn += r.DeskAmount; else CardOut -= r.DeskAmount;
        }

        var desk = PaymentMethods.IsDesk(r.Method);
        switch (r.Type)
        {
            case LedgerTypes.SessionCharge or LedgerTypes.PackagePurchase:
                TimeRows++;
                TimeTotal += r.Amount;
                Time = desk ? Time.Add(r.Method, r.DeskAmount)
                    : r.Method == PaymentMethods.Debt ? Time.Add(PaymentMethods.Debt, r.DebtDelta)
                    : Time with { Balance = Time.Balance - r.BalanceDelta, Bonus = Time.Bonus - r.BonusDelta };
                BonusSpent -= r.BonusDelta;
                if (r.Method == PaymentMethods.Debt) DebtRecorded += r.DebtDelta;
                if (r.SessionId is { } session) _sessions.Add(session);
                break;
            case LedgerTypes.BarSale:
                Receipts++;
                BarSales = BarSales.Add(r.Method, desk ? r.DeskAmount : -r.BalanceDelta);
                break;
            case LedgerTypes.BarRefund:
                RefundReceipts++;
                BarRefunds = BarRefunds.Add(r.Method, desk ? -r.DeskAmount : r.BalanceDelta);
                break;
            case LedgerTypes.TopUp:
                TopUpCount++;
                TopUps = TopUps.Add(r.Method, r.DeskAmount);
                break;
            case LedgerTypes.Refund:
                RefundCount++;
                Refunds = Refunds.Add(r.Method, -r.DeskAmount);
                break;
            case LedgerTypes.BonusAccrual:
                BonusIssued += r.BonusDelta;
                break;
            case LedgerTypes.BonusReversal:
                BonusReversed -= r.BonusDelta;
                break;
            case LedgerTypes.Adjustment:
                BonusAdjusted += r.BonusDelta;
                if (r.BalanceDelta > 0) BalancePlus += r.BalanceDelta; else BalanceMinus -= r.BalanceDelta;
                break;
            case LedgerTypes.DebtPayment:
                DebtPaid = DebtPaid.Add(r.Method, desk ? r.DeskAmount : -r.BalanceDelta);
                break;
            case LedgerTypes.DebtWriteOff:
                DebtWrittenOff -= r.DebtDelta;
                break;
            case LedgerTypes.CashPayout:
                _payouts.Add(new ZPayout(r.Id, r.CreatedUtc, -r.DeskAmount, r.Reason, r.ActorUsername));
                break;
            default:
                OtherRows++;
                if (r.Method == PaymentMethods.Cash) OtherCash += r.DeskAmount;
                if (r.Method == PaymentMethods.Card) OtherCard += r.DeskAmount;
                break;
        }
    }
}
