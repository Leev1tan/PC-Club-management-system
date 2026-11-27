using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Shifts;

/// <summary>
/// Shifts at the desk (spec area 6):
///  - open with an opening float; at most one shift is open (unique index);
///  - while a shift is open, every ledger row gets its id (Ledger.AppendAsync); cash and card rows
///    need an open shift when Shifts:RequireOpenShift is on (default), balance and bonus rows do not;
///  - close with the counted cash: expected cash = float + every cash amount of the shift
///    (cash sales, cash top-ups, cash debt payments - cash refunds - cash payouts); variance =
///    counted - expected. The opener or a manager closes it; a closed shift is final;
///  - cash payouts (collection, supplier) during the shift: Owner, Manager, with a reason, never more
///    than the cash that should be in the drawer.
/// Locks (PostgreSQL): a payment share-locks the open shift row, closing takes it exclusively, so a
/// payment either belongs to the shift and is counted, or it waits and finds the shift closed.
/// </summary>
public sealed class ShiftService
{
    public const decimal MaxAmount = 1_000_000m;

    private readonly CmsDbContext _db;
    private readonly ClubClock _clock;
    private readonly AuditLogger? _audit;
    private readonly StaffActor? _actor;

    public ShiftService(CmsDbContext db, ClubClock? clock = null, AuditLogger? audit = null, StaffActor? actor = null)
    {
        _db = db;
        _clock = clock ?? ClubClock.System();
        _audit = audit;
        _actor = actor;
    }

    /// <summary>Id of the open shift, share-locked until the transaction ends on PostgreSQL (SQLite: one writer at a time).</summary>
    public static async Task<long?> LockOpenShiftIdAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql())
        {
            var ids = await db.Database
                .SqlQueryRaw<long>("SELECT \"Id\" AS \"Value\" FROM \"Shifts\" WHERE \"Status\" = 'open' FOR SHARE")
                .ToListAsync(ct);
            return ids.Count > 0 ? ids[0] : null;
        }
        return await db.Shifts.AsNoTracking().Where(s => s.Status == ShiftStatuses.Open).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
    }

    public static Task<ShiftEntity?> OpenShiftAsync(CmsDbContext db, CancellationToken ct = default) =>
        db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Status == ShiftStatuses.Open, ct);

    /// <summary>Cash that should be in the drawer: float + every cash amount of the shift (sum in memory: SQLite).</summary>
    public static async Task<decimal> ExpectedCashAsync(CmsDbContext db, ShiftEntity shift, CancellationToken ct = default)
    {
        var cash = await db.Ledger.AsNoTracking()
            .Where(l => l.ShiftId == shift.Id && l.Method == PaymentMethods.Cash)
            .Select(l => l.DeskAmount).ToListAsync(ct);
        return shift.OpeningFloat + cash.Sum();
    }

    private Task AuditAsync(string action, long shiftId, string summary, object details) =>
        _audit?.WriteAsync(action, "shift", shiftId, summary, details) ?? Task.CompletedTask;

    public async Task<BillingResult<ShiftEntity>> OpenAsync(decimal openingFloat, string? note)
    {
        var cur = await _db.CurrencySymbolAsync();
        if (openingFloat < 0 || openingFloat > MaxAmount || openingFloat != TariffCalculator.Round(openingFloat))
            return ShiftErrors.BadFloat;
        if (await OpenShiftAsync(_db) != null) return ShiftErrors.AlreadyOpen;

        var now = _clock.UtcNow;
        var shift = new ShiftEntity
        {
            Status = ShiftStatuses.Open, OpenedUtc = now, OpenedById = _actor?.Id, OpenedByUsername = _actor?.Username,
            OpeningFloat = openingFloat, OpenNote = Clean(note)
        };
        var previous = (await _db.Shifts.AsNoTracking().Where(s => s.Status == ShiftStatuses.Closed)
            .OrderByDescending(s => s.Id).Take(1).ToListAsync()).FirstOrDefault();

        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.Shifts.Add(shift);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ShiftErrors.AlreadyOpen; // opened by someone else at the same moment
        }
        await AuditAsync(AuditActions.ShiftOpened, shift.Id, OpenedSummary(shift, previous, cur), OpenedDetails(shift, previous));
        await tx.CommitAsync();
        return BillingResult<ShiftEntity>.Success(shift);
    }

    /// <summary>Closes the open shift with the counted cash (the opener, Owner or Manager).</summary>
    public async Task<BillingResult<ShiftEntity>> CloseAsync(long id, decimal countedCash, string? note, bool isManager)
    {
        var cur = await _db.CurrencySymbolAsync();
        if (countedCash < 0 || countedCash > MaxAmount || countedCash != TariffCalculator.Round(countedCash))
            return ShiftErrors.BadCounted;

        await using var tx = await _db.Database.BeginTransactionAsync();
        // PostgreSQL: wait for payments that are being written to this shift, block new ones
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"Shifts\" WHERE \"Id\" = {0} FOR UPDATE", id);
        var shift = await _db.Shifts.FirstOrDefaultAsync(s => s.Id == id);
        if (shift == null) return ShiftErrors.NotFound;
        if (shift.Status != ShiftStatuses.Open) return ShiftErrors.Closed;
        if (!isManager && (_actor?.Id == null || shift.OpenedById != _actor.Id)) return ShiftErrors.NotYours;

        var expected = await ExpectedCashAsync(_db, shift);
        // Anchor: the last audit row and its hash, printed on the Z-report; a later rewrite of the chain shows
        await AuditChain.LockAsync(_db);
        ApplyClose(shift, countedCash, expected, note, _actor, _clock.UtcNow, await AuditChain.HeadAsync(_db));
        await _db.SaveChangesAsync();

        await AuditAsync(AuditActions.ShiftClosed, shift.Id, ClosedSummary(shift, cur), ClosedDetails(shift));
        await tx.CommitAsync();
        return BillingResult<ShiftEntity>.Success(shift);
    }

    // The close and its audit texts, shared with the demo seed (Demo/DemoSeeder.Bar.cs), so a seeded
    // shift is closed and anchored exactly like a real one.

    /// <summary>
    /// Closes the shift in memory: counted and expected cash, variance (counted - expected), who and when,
    /// and the audit anchor = the last audit row and its hash BEFORE the shift.closed row.
    /// </summary>
    public static void ApplyClose(ShiftEntity shift, decimal countedCash, decimal expectedCash, string? note, StaffActor? actor,
        DateTimeOffset now, (long? Id, string? Hash) auditHead)
    {
        shift.Status = ShiftStatuses.Closed;
        shift.ClosedUtc = now;
        shift.ClosedById = actor?.Id;
        shift.ClosedByUsername = actor?.Username;
        shift.CountedCash = countedCash;
        shift.ExpectedCash = expectedCash;
        shift.Variance = countedCash - expectedCash;
        shift.CloseNote = Clean(note);
        (shift.AuditHeadId, shift.AuditHeadHash) = auditHead;
    }

    public static string OpenedSummary(ShiftEntity shift, ShiftEntity? previous, string cur) =>
        $"Shift #{shift.Id} opened with a float of {MoneyText.Of(shift.OpeningFloat)} {cur}" +
        (previous?.CountedCash is { } left && left != shift.OpeningFloat ? $" (shift #{previous.Id} closed with {MoneyText.Of(left)} {cur} counted)" : "");

    public static object OpenedDetails(ShiftEntity shift, ShiftEntity? previous) =>
        new { shift.OpeningFloat, note = shift.OpenNote, previousShift = previous?.Id, previousCountedCash = previous?.CountedCash };

    public static string ClosedSummary(ShiftEntity shift, string cur) =>
        $"Shift #{shift.Id} closed: counted {MoneyText.Of(shift.CountedCash)} {cur}, expected {MoneyText.Of(shift.ExpectedCash)} {cur}, variance {shift.Variance:+0.00;-0.00;0.00} {cur}";

    public static object ClosedDetails(ShiftEntity shift) =>
        new { shift.OpeningFloat, countedCash = shift.CountedCash, expectedCash = shift.ExpectedCash, variance = shift.Variance, note = shift.CloseNote, openedBy = shift.OpenedByUsername };

    /// <summary>Cash out of the drawer during the open shift (Owner, Manager), with a reason.</summary>
    public async Task<BillingResult<LedgerEntryEntity>> PayoutAsync(long id, decimal amount, string? reason)
    {
        var cur = await _db.CurrencySymbolAsync();
        if (amount <= 0 || amount > MaxAmount || amount != TariffCalculator.Round(amount)) return BillingErrors.BadAmount;
        var why = Clean(reason);
        if (why is not { Length: >= 3 }) return BillingErrors.ReasonRequired;

        await using var tx = await _db.Database.BeginTransactionAsync();
        var openId = await LockOpenShiftIdAsync(_db);
        var shift = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (shift == null) return ShiftErrors.NotFound;
        if (openId != id) return ShiftErrors.Closed;
        var inDrawer = await ExpectedCashAsync(_db, shift);
        if (amount > inDrawer) return ShiftErrors.CashTooLow.With(inDrawer);

        var row = await Ledger.AppendAsync(_db, new LedgerEntryEntity
        {
            Type = LedgerTypes.CashPayout, Method = PaymentMethods.Cash, Amount = amount, DeskAmount = -amount,
            ShiftId = id, Reason = why, Description = $"Виплата з каси {MoneyText.Of(amount)} {cur}: {why}"
        }, _actor, _clock.UtcNow);
        await AuditAsync(AuditActions.ShiftCashPayout, id, $"Cash payout {MoneyText.Of(amount)} {cur} from shift #{id}: {why}", new { amount, reason = why, ledgerRow = row.Id });
        await tx.CommitAsync();
        return BillingResult<LedgerEntryEntity>.Success(row);
    }

    private static string? Clean(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > 300 ? t[..300] : t;
    }
}

public static class ShiftErrors
{
    public static readonly BillingError Required = new("shift_required", "Open a shift first: cash and card payments need an open shift.", StatusCodes.Status409Conflict);
    public static readonly BillingError AlreadyOpen = new("shift_already_open", "A shift is already open. Close it first.", StatusCodes.Status409Conflict);
    public static readonly BillingError NotFound = new("shift_not_found", "Shift not found", StatusCodes.Status404NotFound);
    public static readonly BillingError Closed = new("shift_closed", "The shift is closed. A closed shift cannot be changed.", StatusCodes.Status409Conflict);
    public static readonly BillingError NotYours = new("shift_not_yours", "Only the cashier who opened the shift, a manager or the owner can close it.", StatusCodes.Status403Forbidden);
    public static readonly BillingError BadFloat = new("bad_float", "The opening float must be between 0 and 1000000.");
    public static readonly BillingError BadCounted = new("bad_counted", "The counted cash must be between 0 and 1000000.");
    public static readonly BillingError CashTooLow = new("cash_too_low", "The drawer should hold only {0}.", StatusCodes.Status409Conflict);
    public static readonly BillingError ReportAfterClose = new("shift_report_after_close", "A cashier sees the Z-report of an own shift after the close (blind close).", StatusCodes.Status403Forbidden);
}
