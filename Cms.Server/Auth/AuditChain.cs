using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Auth;

/// <summary>
/// Hash chain of the audit log (tamper evidence). Each row stores
///   Hash = SHA-256( PrevHash + "\n" + canonical content of the row ), hex, lower case
/// where PrevHash is the Hash of the row before it (64 zeros for the first row) and the canonical
/// content is the JSON array ["v1", CreatedUtc (UTC, microseconds), ActorId, ActorUsername, ActorRole,
/// Action, TargetType, TargetId, Summary, DetailsJson, Ip]. A changed row breaks its own hash; a
/// removed or inserted row breaks the link of the next row. Rows are appended one at a time
/// (PostgreSQL: transaction advisory lock; SQLite: one writer), so the chain stays linear.
/// Limit: someone with write access to the database can recompute the whole chain; the head hash
/// printed on every Z-report (and checked against the shift) shows such a rewrite afterwards.
/// </summary>
public static class AuditChain
{
    public static readonly string Genesis = new('0', 64);
    private const long AdvisoryLockKey = 0x43434D5341554449; // "CCMSAUDI"

    /// <summary>UTC, truncated to whole microseconds (what PostgreSQL stores).</summary>
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    public static string Canonical(AuditLogEntity e) => JsonSerializer.Serialize(new[]
    {
        "v1",
        Normalize(e.CreatedUtc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        e.ActorId?.ToString("D"), e.ActorUsername, e.ActorRole, e.Action, e.TargetType, e.TargetId, e.Summary, e.DetailsJson, e.Ip
    });

    public static string Compute(string prevHash, AuditLogEntity e) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prevHash + "\n" + Canonical(e)))).ToLowerInvariant();

    /// <summary>Holds the chain until the transaction ends (PostgreSQL); SQLite transactions already run one at a time.</summary>
    public static async Task LockAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
    }

    /// <summary>Id and hash of the last row (null on an empty log).</summary>
    public static async Task<(long? Id, string? Hash)> HeadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var head = await db.AuditLog.AsNoTracking().OrderByDescending(a => a.Id).Select(a => new { a.Id, a.Hash }).FirstOrDefaultAsync(ct);
        return (head?.Id, head?.Hash);
    }

    /// <summary>Appends the row with its hash; uses the caller's transaction or its own.</summary>
    public static async Task AppendAsync(CmsDbContext db, AuditLogEntity entry, CancellationToken ct = default)
    {
        var own = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await LockAsync(db, ct);
            entry.CreatedUtc = Normalize(entry.CreatedUtc == default ? DateTimeOffset.UtcNow : entry.CreatedUtc);
            entry.PrevHash = (await HeadAsync(db, ct)).Hash ?? Genesis;
            entry.Hash = Compute(entry.PrevHash, entry);
            db.AuditLog.Add(entry);
            await db.SaveChangesAsync(ct);
            if (own != null) await own.CommitAsync(ct);
        }
        finally
        {
            if (own != null) await own.DisposeAsync();
        }
    }

    /// <summary>
    /// Upgrade from a version without the chain: hashes the existing rows once, in id order (the
    /// append-only trigger is paused inside the transaction). Runs only while no row has a hash and
    /// the club setting audit.chain_sealed is missing; returns the number of rows sealed.
    /// </summary>
    public static async Task<int> SealLegacyAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (await db.ClubSettings.AnyAsync(s => s.Key == ClubSettingKeys.AuditChainSealed, ct)) return 0;
        if (await db.AuditLog.AnyAsync(a => a.Hash != null, ct)) return 0; // already chained: never rewrite hashes

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        if (rows.Count > 0)
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLog\" DISABLE TRIGGER audit_log_append_only", ct);
            else
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS \"AuditLog_no_update\"", ct);
            var prev = Genesis;
            foreach (var row in rows)
            {
                var hash = Compute(prev, row);
                var p = prev;
                await db.AuditLog.Where(a => a.Id == row.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.PrevHash, p).SetProperty(a => a.Hash, hash), ct);
                prev = hash;
            }
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLog\" ENABLE TRIGGER audit_log_append_only", ct);
            else
                await AuditLogGuard.EnsureSqliteTriggersAsync(db, ct);
        }
        db.ClubSettings.Add(new ClubSettingEntity { Key = ClubSettingKeys.AuditChainSealed, Value = $"{rows.Count} rows", UpdatedUtc = DateTimeOffset.UtcNow, UpdatedBy = "system" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Count;
    }

    public sealed record VerifyResult(bool Ok, int Rows, long? HeadId, string? HeadHash, long? FirstBrokenId, string? Problem, int AnchorsChecked);

    /// <summary>
    /// Recomputes the chain in id order and reports the first broken row: problem "hash" (the row was
    /// changed), "link" (a row before it was removed, inserted or changed), "missing" (a row without a
    /// hash), or "anchor" (the head hash stored by a closed shift does not match: the chain was rewritten).
    /// </summary>
    public static async Task<VerifyResult> VerifyAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var prev = Genesis;
        var count = 0;
        long? headId = null;
        var hashes = new Dictionary<long, string>();
        await foreach (var row in db.AuditLog.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            count++;
            if (row.Hash == null) return new VerifyResult(false, count, headId, prev, row.Id, "missing", 0);
            if (row.PrevHash != prev) return new VerifyResult(false, count, headId, prev, row.Id, "link", 0);
            if (Compute(prev, row) != row.Hash) return new VerifyResult(false, count, headId, prev, row.Id, "hash", 0);
            prev = row.Hash;
            headId = row.Id;
            hashes[row.Id] = row.Hash;
        }

        var anchors = await db.Shifts.AsNoTracking().Where(s => s.AuditHeadId != null).Select(s => new { s.AuditHeadId, s.AuditHeadHash }).ToListAsync(ct);
        foreach (var anchor in anchors.OrderBy(a => a.AuditHeadId))
        {
            if (!hashes.TryGetValue(anchor.AuditHeadId!.Value, out var hash) || hash != anchor.AuditHeadHash)
                return new VerifyResult(false, count, headId, headId == null ? null : prev, anchor.AuditHeadId, "anchor", anchors.Count);
        }
        return new VerifyResult(true, count, headId, headId == null ? null : prev, null, null, anchors.Count);
    }
}
