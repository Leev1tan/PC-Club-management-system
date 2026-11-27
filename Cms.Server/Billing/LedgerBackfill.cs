using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>
/// One-time copy of the balances of a version before the ledger (phase 1-2) into LedgerEntries, so
/// that every balance equals the sum of its ledger rows from the first start of this version on:
///  - per player, an "opening_balance" row (dated before the old history) with the part of the
///    balance that the old history does not explain, and the whole bonus balance;
///  - every row of the old Transactions table, with its date, type and description.
/// Runs at start-up (both databases) until it is done once (club setting ledger.backfilled).
/// </summary>
public static class LedgerBackfill
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        LedgerTypes.TopUp, LedgerTypes.SessionCharge, LedgerTypes.Adjustment
    };

    public static async Task<int> RunAsync(CmsDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        if (await db.ClubSettings.AnyAsync(s => s.Key == ClubSettingKeys.LedgerBackfilled, ct)) return 0;

        var users = await db.Users.AsNoTracking().ToListAsync(ct);
        var legacy = (await db.Transactions.AsNoTracking().ToListAsync(ct))
            .OrderBy(t => t.CreatedUtc).ThenBy(t => t.Id).ToList();
        var needed = !await db.Ledger.AnyAsync(ct) &&
                     (legacy.Count > 0 || users.Any(u => u.Balance != 0 || u.BonusPoints != 0));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.SkipShiftCheck = true; // old cash top-ups have no shift
        var rows = 0;
        if (needed)
        {
            var byUser = legacy.GroupBy(t => t.UserId).ToDictionary(g => g.Key, g => g.ToList());
            var planned = new List<LedgerEntryEntity>();
            foreach (var user in users)
            {
                var history = byUser.TryGetValue(user.Id, out var list) ? list : new List<TransactionEntity>();
                var explained = history.Sum(t => t.Amount);
                var opening = user.Balance - explained;
                var running = 0m;
                if (opening != 0 || user.BonusPoints != 0)
                {
                    var first = history.Count > 0 ? history[0].CreatedUtc : user.CreatedUtc;
                    running = opening;
                    planned.Add(new LedgerEntryEntity
                    {
                        CreatedUtc = (first < user.CreatedUtc ? first : user.CreatedUtc).AddSeconds(-1),
                        UserId = user.Id, Type = LedgerTypes.OpeningBalance, Method = PaymentMethods.None,
                        Amount = Math.Abs(opening) + user.BonusPoints, BalanceDelta = opening, BonusDelta = user.BonusPoints,
                        BalanceAfter = opening, BonusAfter = user.BonusPoints,
                        Description = "Залишок з попередньої версії (до журналу операцій)"
                    });
                }
                foreach (var t in history)
                {
                    running += t.Amount;
                    var type = KnownTypes.Contains(t.Type) ? t.Type.ToLowerInvariant() : LedgerTypes.Legacy;
                    planned.Add(new LedgerEntryEntity
                    {
                        CreatedUtc = t.CreatedUtc, UserId = user.Id, Type = type,
                        Method = type == LedgerTypes.TopUp ? PaymentMethods.Cash
                            : type == LedgerTypes.SessionCharge ? PaymentMethods.Balance : PaymentMethods.None,
                        Amount = Math.Abs(t.Amount), BalanceDelta = t.Amount,
                        DeskAmount = type == LedgerTypes.TopUp ? t.Amount : 0m,
                        BalanceAfter = running, BonusAfter = user.BonusPoints,
                        Description = t.Description, Reason = type == LedgerTypes.Adjustment ? t.Description : null
                    });
                }
            }
            // Ids follow the dates: save in date order, one row at a time (one-time job)
            foreach (var row in planned.OrderBy(r => r.CreatedUtc))
            {
                db.Ledger.Add(row);
                await db.SaveChangesAsync(ct);
                rows++;
            }
        }

        db.ClubSettings.Add(new ClubSettingEntity
        {
            Key = ClubSettingKeys.LedgerBackfilled, Value = $"{rows} rows", UpdatedUtc = now, UpdatedBy = "system"
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.SkipShiftCheck = false;
        db.ChangeTracker.Clear();
        return rows;
    }
}
