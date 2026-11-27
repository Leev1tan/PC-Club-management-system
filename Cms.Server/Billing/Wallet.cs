using System.Security.Claims;
using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>Ledger row types (LedgerEntryEntity.Type).</summary>
public static class LedgerTypes
{
    /// <summary>Money paid in at the desk onto the real balance (+balance, +desk).</summary>
    public const string TopUp = "topup";
    /// <summary>Money paid back from the real balance (-balance, -desk). Bonus is never paid out.</summary>
    public const string Refund = "refund";
    /// <summary>Correction by a manager with a mandatory reason (+/- balance or bonus).</summary>
    public const string Adjustment = "adjustment";
    /// <summary>PC time: from the balances (bonus first) or paid at the desk (guests).</summary>
    public const string SessionCharge = "session_charge";
    /// <summary>A package: from the balances (bonus first) or paid at the desk.</summary>
    public const string PackagePurchase = "package_purchase";
    /// <summary>Cashback on a top-up (+bonus).</summary>
    public const string BonusAccrual = "bonus_accrual";
    /// <summary>Cashback taken back when the top-up is refunded (-bonus).</summary>
    public const string BonusReversal = "bonus_reversal";
    /// <summary>Balances of a version before the ledger (written once by the backfill).</summary>
    public const string OpeningBalance = "opening_balance";
    /// <summary>History row of a version before the ledger with an unknown type.</summary>
    public const string Legacy = "legacy";
}

public static class PaymentMethods
{
    public const string Balance = "balance";
    public const string Cash = "cash";
    public const string Card = "card";
    public const string None = "none";

    /// <summary>balance, cash or card; null for an empty value; "" for an unknown value.</summary>
    public static string? Normalize(string? method)
    {
        var m = method?.Trim().ToLowerInvariant();
        return m switch
        {
            null or "" => null,
            Balance or Cash or Card => m,
            _ => string.Empty
        };
    }

    public static bool IsDesk(string? method) => method is Cash or Card;
}

/// <summary>What money may pay for. Bonus pays only for PC time (sessions, packages, extensions).</summary>
public enum SpendPurpose { PcTime, Bar }

public sealed record Charge(decimal FromBonus, decimal FromBalance)
{
    public decimal Total => FromBonus + FromBalance;
    public static readonly Charge None = new(0m, 0m);
}

/// <summary>The signed-in staff member, written into ledger rows.</summary>
public sealed record StaffActor(Guid? Id, string? Username)
{
    public static StaffActor? From(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true || user.FindFirstValue(AuthConstants.StampClaim) == null) return null;
        return new StaffActor(Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null, user.Identity!.Name);
    }
}

/// <summary>
/// Atomic changes of the real balance (Users.Balance) and the bonus balance (Users.BonusPoints).
/// Each change is ONE conditional UPDATE, so two cashiers or two PCs cannot spend the same money
/// twice and no balance goes below zero:
///   UPDATE "Users" SET "Balance" = "Balance" - @r, "BonusPoints" = "BonusPoints" - @b
///   WHERE "Id" = @id AND "Balance" >= @r AND "BonusPoints" >= @b
/// PostgreSQL re-checks the condition after a concurrent update commits; SQLite runs one writer at a
/// time. Call inside the transaction that also appends the ledger row (<see cref="Ledger"/>).
/// </summary>
public static class Wallet
{
    private const int Attempts = 8;

    private sealed record Balances(decimal Balance, decimal Bonus);

    private static Task<Balances?> ReadAsync(CmsDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new Balances(u.Balance, u.BonusPoints)).FirstOrDefaultAsync(ct);

    /// <summary>Current balances (real, bonus); null when the player does not exist.</summary>
    public static async Task<(decimal Balance, decimal Bonus)?> GetAsync(CmsDbContext db, Guid userId, CancellationToken ct = default)
    {
        var b = await ReadAsync(db, userId, ct);
        return b == null ? null : (b.Balance, b.Bonus);
    }

    /// <summary>
    /// Takes the whole amount: bonus first (PC time only, when useBonus), the rest from the real
    /// balance. Null when the balances are too low or the player does not exist (nothing changes).
    /// </summary>
    public static async Task<Charge?> TryChargeAsync(CmsDbContext db, Guid userId, decimal amount, SpendPurpose purpose,
        bool useBonus = true, CancellationToken ct = default)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var b = await ReadAsync(db, userId, ct);
            if (b == null) return null;
            if (amount == 0) return Charge.None;
            var fromBonus = purpose == SpendPurpose.PcTime && useBonus ? Math.Min(Math.Max(0m, b.Bonus), amount) : 0m;
            var fromBalance = amount - fromBonus;
            if (fromBalance > b.Balance) return null;
            if (await TryTakeAsync(db, userId, fromBalance, fromBonus, ct)) return new Charge(fromBonus, fromBalance);
            // the balances changed in between: read them again
        }
        return null;
    }

    /// <summary>
    /// Takes as much of the amount as the balances have (post-paid sessions): bonus first (PC time,
    /// when useBonus), then the real balance. The rest is due at the desk. Never below zero.
    /// </summary>
    public static async Task<Charge> ChargeUpToAsync(CmsDbContext db, Guid userId, decimal amount, SpendPurpose purpose,
        bool useBonus = true, CancellationToken ct = default)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        for (var attempt = 0; attempt < Attempts && amount > 0; attempt++)
        {
            var b = await ReadAsync(db, userId, ct);
            if (b == null) return Charge.None;
            var fromBonus = purpose == SpendPurpose.PcTime && useBonus ? Math.Min(Math.Max(0m, b.Bonus), amount) : 0m;
            var fromBalance = Math.Min(Math.Max(0m, b.Balance), amount - fromBonus);
            if (fromBonus + fromBalance <= 0) return Charge.None;
            if (await TryTakeAsync(db, userId, fromBalance, fromBonus, ct)) return new Charge(fromBonus, fromBalance);
        }
        return Charge.None;
    }

    /// <summary>Takes exactly this much from the real balance and the bonus balance; false when too low.</summary>
    public static async Task<bool> TryTakeAsync(CmsDbContext db, Guid userId, decimal fromBalance, decimal fromBonus, CancellationToken ct = default)
    {
        if (fromBalance < 0 || fromBonus < 0) throw new ArgumentOutOfRangeException(nameof(fromBalance));
        var rows = await db.Users
            .Where(u => u.Id == userId && u.Balance >= fromBalance && u.BonusPoints >= fromBonus)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Balance, u => u.Balance - fromBalance)
                .SetProperty(u => u.BonusPoints, u => u.BonusPoints - fromBonus), ct);
        return rows == 1;
    }

    /// <summary>Takes up to this much bonus (cashback reversal); returns the amount taken.</summary>
    public static async Task<decimal> TakeBonusUpToAsync(CmsDbContext db, Guid userId, decimal amount, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < Attempts && amount > 0; attempt++)
        {
            var b = await ReadAsync(db, userId, ct);
            if (b == null) return 0m;
            var part = Math.Min(Math.Max(0m, b.Bonus), amount);
            if (part <= 0) return 0m;
            if (await TryTakeAsync(db, userId, 0m, part, ct)) return part;
        }
        return 0m;
    }

    /// <summary>Adds to the real balance and/or the bonus balance; false when the player does not exist.</summary>
    public static async Task<bool> CreditAsync(CmsDbContext db, Guid userId, decimal toBalance, decimal toBonus = 0m, CancellationToken ct = default)
    {
        if (toBalance < 0 || toBonus < 0 || toBalance + toBonus <= 0) throw new ArgumentOutOfRangeException(nameof(toBalance));
        var rows = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Balance, u => u.Balance + toBalance)
                .SetProperty(u => u.BonusPoints, u => u.BonusPoints + toBonus), ct);
        return rows == 1;
    }
}

/// <summary>Appends ledger rows (LedgerEntries is append-only, see LedgerGuard).</summary>
public static class Ledger
{
    /// <summary>
    /// Adds the row and saves. For a player row, BalanceAfter / BonusAfter are read right after the
    /// balance change of the same transaction (the row lock keeps them stable until commit).
    /// </summary>
    public static async Task<LedgerEntryEntity> AppendAsync(CmsDbContext db, LedgerEntryEntity entry, StaffActor? actor,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (entry.UserId.HasValue)
        {
            // A row without a balance change (player pays at the desk) locks the player row too, so
            // rows of one player are written one after the other and BalanceAfter forms a chain
            if (entry.BalanceDelta == 0 && entry.BonusDelta == 0)
                await db.Users.Where(u => u.Id == entry.UserId.Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.Balance, u => u.Balance), ct);
            var balances = await Wallet.GetAsync(db, entry.UserId.Value, ct);
            entry.BalanceAfter = balances?.Balance;
            entry.BonusAfter = balances?.Bonus;
        }
        if (entry.CreatedUtc == default) entry.CreatedUtc = now;
        entry.ActorId ??= actor?.Id;
        entry.ActorUsername ??= actor?.Username;
        if (entry.Description is { Length: > 500 }) entry.Description = entry.Description[..500];
        if (entry.Reason is { Length: > 300 }) entry.Reason = entry.Reason[..300];
        db.Ledger.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Checks the rows of one player in id order: each BalanceAfter / BonusAfter must equal the
    /// previous one plus the row's deltas. Returns the id of the first row that breaks the chain, or null.
    /// </summary>
    public static async Task<long?> CheckChainAsync(CmsDbContext db, Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Ledger.AsNoTracking().Where(l => l.UserId == userId).OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.BalanceDelta, l.BonusDelta, l.BalanceAfter, l.BonusAfter }).ToListAsync(ct);
        decimal balance = 0, bonus = 0;
        foreach (var r in rows)
        {
            balance += r.BalanceDelta;
            bonus += r.BonusDelta;
            if (r.BalanceAfter != balance || r.BonusAfter != bonus) return r.Id;
        }
        return null;
    }

    /// <summary>A player whose cached balances differ from the sum of the ledger rows.</summary>
    public sealed record Mismatch(Guid UserId, string Username, decimal Balance, decimal LedgerBalance, decimal Bonus, decimal LedgerBonus);

    /// <summary>
    /// Compares Users.Balance / BonusPoints with the sums of the ledger (all players, or one).
    /// Sums are made in memory: SQLite cannot add up decimal columns exactly.
    /// </summary>
    public static async Task<List<Mismatch>> ReconcileAsync(CmsDbContext db, Guid? userId = null, CancellationToken ct = default)
    {
        var users = await db.Users.AsNoTracking()
            .Where(u => userId == null || u.Id == userId)
            .Select(u => new { u.Id, u.Username, u.Balance, u.BonusPoints })
            .ToListAsync(ct);
        var rows = await db.Ledger.AsNoTracking()
            .Where(l => l.UserId != null && (userId == null || l.UserId == userId))
            .Select(l => new { l.UserId, l.BalanceDelta, l.BonusDelta })
            .ToListAsync(ct);
        var sums = rows.GroupBy(r => r.UserId!.Value)
            .ToDictionary(g => g.Key, g => (Balance: g.Sum(r => r.BalanceDelta), Bonus: g.Sum(r => r.BonusDelta)));

        var result = new List<Mismatch>();
        foreach (var u in users)
        {
            var (balance, bonus) = sums.TryGetValue(u.Id, out var s) ? s : (0m, 0m);
            if (balance != u.Balance || bonus != u.BonusPoints)
                result.Add(new Mismatch(u.Id, u.Username, u.Balance, balance, u.BonusPoints, bonus));
        }
        return result;
    }
}

/// <summary>Database triggers that make LedgerEntries append-only (SQLite here; PostgreSQL: migration Billing).</summary>
public static class LedgerGuard
{
    private const string SqliteCreateTriggers = """
        CREATE TRIGGER IF NOT EXISTS "LedgerEntries_no_update" BEFORE UPDATE ON "LedgerEntries"
        BEGIN SELECT RAISE(ABORT, 'LedgerEntries is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS "LedgerEntries_no_delete" BEFORE DELETE ON "LedgerEntries"
        BEGIN SELECT RAISE(ABORT, 'LedgerEntries is append-only'); END;
        """;

    public static Task EnsureSqliteTriggersAsync(CmsDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(SqliteCreateTriggers, ct);

    /// <summary>Demo reset only. PostgreSQL: TRUNCATE (row triggers do not fire); SQLite: drop, delete, re-create.</summary>
    public static async Task ClearForDemoResetAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"LedgerEntries\"", ct);
            return;
        }
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS \"LedgerEntries_no_update\"; DROP TRIGGER IF EXISTS \"LedgerEntries_no_delete\";", ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"LedgerEntries\"", ct);
            await EnsureSqliteTriggersAsync(db, ct);
            return;
        }
        await db.Ledger.ExecuteDeleteAsync(ct);
    }
}
