using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Data;

/// <summary>
/// Atomic balance changes. Each change is ONE conditional UPDATE in the database, so two cashiers or
/// two PCs cannot spend the same money twice, and a debit never makes a balance negative:
///   UPDATE "Users" SET "Balance" = "Balance" - @amount WHERE "Id" = @id AND "Balance" >= @amount
/// PostgreSQL re-checks the condition after a concurrent update commits; SQLite runs one writer at
/// a time. Call inside the same transaction as the ledger row and the session change.
/// </summary>
public static class Balances
{
    /// <summary>Takes the amount from the balance; false when the balance is too low (nothing changes).</summary>
    public static async Task<bool> TryDebitAsync(CmsDbContext db, Guid userId, decimal amount, CancellationToken ct = default)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount == 0) return await db.Users.AnyAsync(u => u.Id == userId, ct);
        var rows = await db.Users
            .Where(u => u.Id == userId && u.Balance >= amount)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Balance, u => u.Balance - amount), ct);
        return rows == 1;
    }

    /// <summary>
    /// Takes as much of the amount as the balance has (post-paid sessions). Returns the amount taken;
    /// the rest is due at the desk. The balance never goes below zero.
    /// </summary>
    public static async Task<decimal> DebitUpToAsync(CmsDbContext db, Guid userId, decimal amount, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 5 && amount > 0; attempt++)
        {
            if (await TryDebitAsync(db, userId, amount, ct)) return amount;
            var available = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Balance).FirstOrDefaultAsync(ct);
            var part = Math.Min(available, amount);
            if (part <= 0) return 0;
            if (await TryDebitAsync(db, userId, part, ct)) return part;
            // the balance changed in between: read it again
        }
        return 0;
    }

    public static async Task<bool> CreditAsync(CmsDbContext db, Guid userId, decimal amount, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var rows = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Balance, u => u.Balance + amount), ct);
        return rows == 1;
    }

    public static Task<decimal> GetAsync(CmsDbContext db, Guid userId, CancellationToken ct = default) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Balance).FirstOrDefaultAsync(ct);
}
