using System.Security.Claims;
using System.Text.Json;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Auth;

/// <summary>Audit action names (spec area 6: who unlocked without payment, gave free time, edited balances...).</summary>
public static class AuditActions
{
    public const string Login = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string Logout = "auth.logout";
    public const string PasswordChanged = "auth.password_changed";
    public const string OwnerCreated = "auth.owner_created";

    public const string StaffCreated = "staff.created";
    public const string StaffUpdated = "staff.updated";
    public const string StaffPasswordReset = "staff.password_reset";

    public const string StaffPinChanged = "settings.staff_pin_changed";
    public const string StaffPinRemoved = "settings.staff_pin_removed";
    public const string CurrencyChanged = "settings.currency_changed";
    public const string LanguageChanged = "settings.language_changed";

    public const string DeviceRegistered = "device.registered";
    public const string DeviceApproved = "device.approved";
    public const string DeviceRejected = "device.rejected";
    public const string DeviceRevoked = "device.revoked";
    public const string DeviceZoneChanged = "device.zone_changed";
    public const string DeviceCommand = "device.command";
    /// <summary>Unlock of a PC that has no active session: time without payment.</summary>
    public const string UnlockWithoutSession = "device.unlock_without_session";
    /// <summary>Staff PIN typed on the lock screen (Ctrl+Shift+U), reported by the agent afterwards.</summary>
    public const string EmergencyUnlock = "device.emergency_unlock";

    public const string SessionStarted = "session.started";
    public const string SessionExtended = "session.extended";
    public const string SessionEnded = "session.ended";
    public const string SessionPaused = "session.paused";
    public const string SessionResumed = "session.resumed";
    public const string SessionTransferred = "session.transferred";
    /// <summary>PC time without payment: a free session or extension (with a reason), or a price of 0.</summary>
    public const string SessionFreeTime = "session.free_time";

    public const string PlayerRegistered = "player.registered";
    public const string PlayerTopUp = "player.topup";
    public const string PlayerBalanceAdjusted = "player.balance_adjusted";
    public const string PlayerBonusAdjusted = "player.bonus_adjusted";
    public const string PlayerRefund = "player.refund";
    public const string PlayerPasswordSet = "player.password_set";
    public const string PlayerDebtRecorded = "player.debt_recorded";
    public const string PlayerDebtPaid = "player.debt_paid";
    public const string PlayerDebtWrittenOff = "player.debt_written_off";

    public const string TariffCreated = "tariff.created";
    public const string TariffUpdated = "tariff.updated";
    public const string TariffDeleted = "tariff.deleted";
    public const string PackageCreated = "package.created";
    public const string PackageUpdated = "package.updated";
    public const string PackageDeleted = "package.deleted";
    public const string BillingSettingsChanged = "billing.settings_changed";
    public const string ZoneCreated = "zone.created";
    public const string ZoneUpdated = "zone.updated";
    public const string ZoneDeleted = "zone.deleted";

    public const string ShiftOpened = "shift.opened";
    public const string ShiftClosed = "shift.closed";
    public const string ShiftCashPayout = "shift.cash_payout";

    public const string GameCreated = "game.created";
    public const string GameUpdated = "game.updated";
    public const string GameDeleted = "game.deleted";

    public const string ProductCreated = "product.created";
    public const string ProductUpdated = "product.updated";
    public const string ProductPriceChanged = "product.price_changed";
    public const string ProductDeleted = "product.deleted";
    public const string StockRestocked = "stock.restocked";
    public const string StockCorrected = "stock.corrected";
    public const string SaleRefunded = "sale.refunded";
}

/// <summary>
/// Writes audit entries. The actor is the signed-in staff member of the current request (or the one
/// given). WriteAsync saves at once; inside an open transaction the entry commits with the action.
/// Each entry is a link of the hash chain (AuditChain), so write it as the last step of a transaction.
/// </summary>
public sealed class AuditLogger
{
    // Readable details (Cyrillic, quotes); the admin UI shows them as plain text, never as HTML
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly CmsDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditLogger(CmsDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    public async Task WriteAsync(string action, string? targetType = null, object? targetId = null, string? summary = null,
        object? details = null, StaffUserEntity? actor = null, CancellationToken ct = default)
    {
        var http = _http.HttpContext;
        var user = http?.User;
        var signedIn = user?.Identity?.IsAuthenticated == true && user.FindFirstValue(AuthConstants.StampClaim) != null;

        var entry = new AuditLogEntity
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            ActorId = actor?.Id ?? (signedIn && Guid.TryParse(user!.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null),
            ActorUsername = actor?.Username ?? (signedIn ? user!.Identity!.Name : null),
            ActorRole = actor?.Role ?? (signedIn ? user!.FindFirstValue(ClaimTypes.Role) : null),
            Action = action,
            TargetType = targetType,
            TargetId = Truncate(targetId?.ToString(), 64),
            Summary = Truncate(summary, 500),
            DetailsJson = details == null ? null : Truncate(JsonSerializer.Serialize(details, Json), 4000),
            Ip = Truncate(http?.ClientIp(), 64)
        };
        await AuditChain.AppendAsync(_db, entry, ct); // with its hash (tamper-evident chain)
    }

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..max];
}

/// <summary>Database triggers that make the audit table append-only (SQLite; PostgreSQL: migration AuditLog).</summary>
public static class AuditLogGuard
{
    // PostgreSQL: the trigger is created by the migration AuditLog.

    private const string SqliteCreateTriggers = """
        CREATE TRIGGER IF NOT EXISTS "AuditLog_no_update" BEFORE UPDATE ON "AuditLog"
        BEGIN SELECT RAISE(ABORT, 'AuditLog is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS "AuditLog_no_delete" BEFORE DELETE ON "AuditLog"
        BEGIN SELECT RAISE(ABORT, 'AuditLog is append-only'); END;
        """;

    /// <summary>SQLite has no migrations here; create the triggers after EnsureCreated / upgrade.</summary>
    public static Task EnsureSqliteTriggersAsync(CmsDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(SqliteCreateTriggers, ct);

    /// <summary>
    /// Demo reset only (demo databases are wiped every few hours). PostgreSQL: TRUNCATE (row triggers do
    /// not fire); SQLite: drop the triggers, delete, create them again, inside the caller's transaction.
    /// </summary>
    public static async Task ClearForDemoResetAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"AuditLog\"", ct);
            return;
        }
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS \"AuditLog_no_update\"; DROP TRIGGER IF EXISTS \"AuditLog_no_delete\";", ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLog\"", ct);
            await EnsureSqliteTriggersAsync(db, ct);
            return;
        }
        await db.AuditLog.ExecuteDeleteAsync(ct);
    }
}
