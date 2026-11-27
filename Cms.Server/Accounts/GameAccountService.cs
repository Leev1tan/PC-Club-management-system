using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Devices;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Accounts;

public static class AccountErrors
{
    private const int NotFound = StatusCodes.Status404NotFound;
    private const int Conflict = StatusCodes.Status409Conflict;

    public static readonly BillingError AccountNotFound = new("account_not_found", "Account not found", NotFound);
    public static readonly BillingError BadPlatform = new("account_bad_platform", "Choose a platform: {0}.");
    public static readonly BillingError BadLogin = new("account_bad_login", "The login must be one line of 1 to 100 characters.");
    public static readonly BillingError BadSecret = new("account_bad_secret", "The secret must be one line of 1 to 200 characters.");
    public static readonly BillingError LoginExists = new("account_login_exists", "This platform already has an account with this login.", Conflict);
    public static readonly BillingError Removed = new("account_removed", "The account was removed.", Conflict);
    public static readonly BillingError NotLeased = new("account_not_leased", "No PC uses this account now.", Conflict);
    public static readonly BillingError NeedsSession = new("account_needs_session", "A club account is given only during a session of this PC.", Conflict);
    public static readonly BillingError GameNeedsNone = new("game_needs_no_account", "This game needs no club account.");
    public static readonly BillingError NoFreeAccount = new("no_free_account", "All {1} club account(s) of {0} are in use. Ask the staff.", Conflict);
    public static readonly BillingError LeaseNotFound = new("lease_not_found", "Lease not found", NotFound);
    public static readonly BillingError Delivered = new("credential_delivered", "The credentials of this lease were handed out already.", Conflict);
    public static readonly BillingError Unreadable = new("credential_unreadable", "The server cannot read the secret (its keys changed). Rotate the secret.", Conflict);
}

public sealed record AddGameAccountRequest(string? Platform, string? Login, string? Secret = null, bool IsActive = true);
public sealed record RotateSecretRequest(string? Secret);
public sealed record AccountActiveRequest(bool IsActive);
public sealed record LeaseRequest(Guid GameId);

/// <summary>The current lease of an account: the PC, the player's nickname, the game, since when, handed out or not.</summary>
public sealed record AccountLeaseView(long Id, Guid DeviceId, string Hostname, Guid SessionId, string? Username, string? Game, DateTimeOffset StartedUtc, bool Delivered);
/// <summary>An account for staff: never the login or the secret; HasSecret tells whether a secret is stored.</summary>
public sealed record GameAccountView(Guid Id, string Platform, string LoginMask, bool HasSecret, bool IsActive, bool Removed,
    DateTimeOffset CreatedUtc, DateTimeOffset? SecretUpdatedUtc, AccountLeaseView? Lease, int Leases);
/// <summary>The lease of the agent: Delivered = the credentials were fetched already (the client is logged in).</summary>
public sealed record DeviceLeaseView(long LeaseId, string Platform, string LoginMask, bool Delivered);
/// <summary>The credentials, once per lease, only for the agent of the leased PC.</summary>
public sealed record DeviceCredentialView(long LeaseId, string Platform, string Login, string Secret);

/// <summary>
/// The club game-account pool (spec area 8). Owner and Manager add, rotate, disable and remove accounts; the front desk
/// sees which PC uses which account (masked login) and frees a stuck one. When a player opens a game that needs a club
/// account during a session, the agent of the PC asks for a lease: the server gives a free account of that platform to
/// that PC for the session (database indexes: one open lease per account, one per PC and platform; two PCs at the same
/// moment never get the same account). The agent then fetches the credentials once (device key). The lease ends with the
/// session (end, expiry, logout, a transfer away from the PC), when staff free it, or when the server finds its session
/// gone (every 5 s and at the start). Audit texts name the platform and the masked login, never the login or the secret.
/// </summary>
public sealed class GameAccountService
{
    public const int MaxLogin = 100;
    public const int MaxSecret = 200;
    private const int LeaseAttempts = 20;

    private readonly CmsDbContext _db;
    private readonly GameAccountVault _vault;
    private readonly ClubClock _clock;
    private readonly AuditLogger? _audit;
    private readonly StaffActor? _actor;

    public GameAccountService(CmsDbContext db, GameAccountVault vault, ClubClock? clock = null, AuditLogger? audit = null, StaffActor? actor = null)
    {
        _db = db;
        _vault = vault;
        _clock = clock ?? ClubClock.System();
        _audit = audit;
        _actor = actor;
    }

    private Task AuditAsync(string action, Guid id, string summary, object details) =>
        _audit?.WriteAsync(action, "game_account", id, summary, details) ?? Task.CompletedTask;

    /// <summary>"de•••01": two characters at each end of a long login, one at the start of a short one.</summary>
    public static string Mask(string login)
    {
        var l = login.Trim();
        if (l.Length <= 2) return "•••";
        if (l.Length <= 5) return l[..1] + "•••";
        return l[..2] + "•••" + l[^2..];
    }

    private static string? CleanLine(string? value, int max)
    {
        if (value == null) return null;
        var v = value.Trim();
        return v.Length == 0 || v.Length > max || v.Any(char.IsControl) ? null : v;
    }

    private static string Name(GameAccountEntity a) => $"{a.Platform} {a.LoginMask}";

    // ===== Views =====

    public static async Task<List<GameAccountView>> ViewsAsync(CmsDbContext db, Guid? id = null, CancellationToken ct = default)
    {
        var accounts = await db.GameAccounts.AsNoTracking().Where(a => id == null || a.Id == id).ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var open = await db.GameAccountLeases.AsNoTracking().Where(l => ids.Contains(l.AccountId) && l.EndedUtc == null).ToListAsync(ct);
        var counts = await db.GameAccountLeases.AsNoTracking().Where(l => ids.Contains(l.AccountId)).GroupBy(l => l.AccountId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var deviceIds = open.Select(l => l.DeviceId).Distinct().ToList();
        var hosts = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Hostname, ct);
        var userIds = open.Where(l => l.UserId.HasValue).Select(l => l.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        var gameIds = open.Where(l => l.GameId.HasValue).Select(l => l.GameId!.Value).Distinct().ToList();
        var games = await db.Games.AsNoTracking().Where(g => gameIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return accounts
            .OrderBy(a => a.RemovedUtc.HasValue).ThenBy(a => a.Platform).ThenBy(a => a.CreatedUtc)
            .Select(a =>
            {
                var l = open.FirstOrDefault(x => x.AccountId == a.Id);
                return new GameAccountView(a.Id, a.Platform, a.LoginMask, a.SecretCipher != null, a.IsActive, a.RemovedUtc.HasValue, a.CreatedUtc, a.SecretUpdatedUtc,
                    l == null ? null : new AccountLeaseView(l.Id, l.DeviceId, hosts.GetValueOrDefault(l.DeviceId) ?? "?", l.SessionId,
                        l.UserId is { } u && users.TryGetValue(u, out var name) ? name : null, l.GameId is { } g && games.TryGetValue(g, out var gn) ? gn : null,
                        l.StartedUtc, l.DeliveredUtc.HasValue),
                    counts.GetValueOrDefault(a.Id));
            }).ToList();
    }

    private async Task<GameAccountView> ViewAsync(Guid id) => (await ViewsAsync(_db, id)).Single();

    // ===== Staff: add, rotate, disable, remove, free =====

    public async Task<BillingResult<GameAccountView>> AddAsync(AddGameAccountRequest req)
    {
        var platform = AccountPlatforms.Normalize(req.Platform);
        if (platform == null) return AccountErrors.BadPlatform.With(string.Join(", ", AccountPlatforms.All));
        var login = CleanLine(req.Login, MaxLogin);
        if (login == null) return AccountErrors.BadLogin;
        string? secret = null;
        if (!string.IsNullOrEmpty(req.Secret))
        {
            secret = CleanLine(req.Secret, MaxSecret);
            if (secret == null) return AccountErrors.BadSecret;
        }
        // The logins are encrypted: compare them in memory (a club has few accounts)
        var same = await _db.GameAccounts.AsNoTracking().Where(a => a.Platform == platform && a.RemovedUtc == null).Select(a => a.LoginCipher).ToListAsync();
        if (same.Any(c => string.Equals(_vault.TryUnprotect(c), login, StringComparison.OrdinalIgnoreCase))) return AccountErrors.LoginExists;
        var now = _clock.UtcNow;
        var account = new GameAccountEntity
        {
            Id = Guid.NewGuid(), Platform = platform, LoginMask = Mask(login), LoginCipher = _vault.Protect(login),
            SecretCipher = secret == null ? null : _vault.Protect(secret), IsActive = req.IsActive,
            CreatedUtc = now, UpdatedUtc = now, SecretUpdatedUtc = secret == null ? null : now, CreatedByUsername = _actor?.Username
        };
        _db.GameAccounts.Add(account);
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.GameAccountAdded, account.Id,
            $"Club account {Name(account)} added" + (secret == null ? " without a secret" : "") + (account.IsActive ? "" : " (off)"),
            new { account.Platform, login = account.LoginMask, hasSecret = secret != null, account.IsActive });
        return BillingResult<GameAccountView>.Success(await ViewAsync(account.Id));
    }

    /// <summary>A new secret (write-only); the old one is gone. A PC that uses the account keeps its lease.</summary>
    public async Task<BillingResult<GameAccountView>> RotateAsync(Guid id, RotateSecretRequest req)
    {
        var secret = CleanLine(req.Secret, MaxSecret);
        if (secret == null) return AccountErrors.BadSecret;
        var account = await _db.GameAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account == null) return AccountErrors.AccountNotFound;
        if (account.RemovedUtc != null) return AccountErrors.Removed;
        var now = _clock.UtcNow;
        var had = account.SecretCipher != null;
        account.SecretCipher = _vault.Protect(secret);
        account.SecretUpdatedUtc = now;
        account.UpdatedUtc = now;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.GameAccountRotated, account.Id, $"Secret of club account {Name(account)} {(had ? "rotated" : "set")}",
            new { account.Platform, login = account.LoginMask });
        return BillingResult<GameAccountView>.Success(await ViewAsync(account.Id));
    }

    public async Task<BillingResult<GameAccountView>> SetActiveAsync(Guid id, bool active)
    {
        var account = await _db.GameAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account == null) return AccountErrors.AccountNotFound;
        if (account.RemovedUtc != null) return AccountErrors.Removed;
        if (account.IsActive == active) return BillingResult<GameAccountView>.Success(await ViewAsync(id));
        account.IsActive = active;
        account.UpdatedUtc = _clock.UtcNow;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.GameAccountUpdated, account.Id, $"Club account {Name(account)} {(active ? "on" : "off")} (a PC that uses it keeps it until the session ends)",
            new { account.Platform, login = account.LoginMask, isActive = active });
        return BillingResult<GameAccountView>.Success(await ViewAsync(id));
    }

    /// <summary>Removes an account: its lease ends, the encrypted login and secret are deleted; the row stays for the lease history.</summary>
    public async Task<BillingResult<GameAccountView>> RemoveAsync(Guid id)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.GameAccounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account == null) return AccountErrors.AccountNotFound;
        if (account.RemovedUtc != null) return AccountErrors.Removed;
        var ended = await EndLeasesAsync(_db, l => l.AccountId == id, now, "removed", _actor?.Username);
        account.RemovedUtc = now;
        account.UpdatedUtc = now;
        account.IsActive = false;
        account.LoginCipher = string.Empty;
        account.SecretCipher = null;
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.GameAccountRemoved, account.Id, $"Club account {Name(account)} removed" + (ended > 0 ? ", its lease ended" : ""),
            new { account.Platform, login = account.LoginMask, leaseEnded = ended > 0 });
        await tx.CommitAsync();
        return BillingResult<GameAccountView>.Success(await ViewAsync(id));
    }

    /// <summary>Staff free an account that a PC keeps (a stuck lease): the lease ends now.</summary>
    public async Task<BillingResult<GameAccountView>> ReleaseAsync(Guid id)
    {
        var account = await _db.GameAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (account == null) return AccountErrors.AccountNotFound;
        var lease = await _db.GameAccountLeases.AsNoTracking().FirstOrDefaultAsync(l => l.AccountId == id && l.EndedUtc == null);
        if (lease == null) return AccountErrors.NotLeased;
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (await EndLeasesAsync(_db, l => l.Id == lease.Id, _clock.UtcNow, "staff", _actor?.Username) == 0) return AccountErrors.NotLeased;
        var host = await _db.Devices.AsNoTracking().Where(d => d.Id == lease.DeviceId).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";
        await AuditAsync(AuditActions.GameAccountReleased, account.Id, $"Club account {Name(account)} freed from {host} by staff (lease #{lease.Id})",
            new { account.Platform, login = account.LoginMask, lease = lease.Id, device = host, delivered = lease.DeliveredUtc.HasValue });
        await tx.CommitAsync();
        return BillingResult<GameAccountView>.Success(await ViewAsync(id));
    }

    // ===== The agent of a PC: lease, credentials =====

    /// <summary>
    /// A lease for this PC and game: the open lease of the PC for the platform (same session), or a free account of the
    /// platform. Errors: no session, a game without an account platform or not in the catalog of the PC, no free account.
    /// </summary>
    public async Task<BillingResult<DeviceLeaseView>> LeaseAsync(Guid deviceId, Guid gameId)
    {
        var now = _clock.UtcNow;
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId);
        if (device == null) return BillingErrors.DeviceNotFound;
        var session = (await _db.Sessions.AsNoTracking().Where(s => s.DeviceId == deviceId && s.Status == "active").ToListAsync())
            .FirstOrDefault(s => !SessionService.IsDue(s, now) && s.PausedUtc == null);
        if (session == null) return AccountErrors.NeedsSession;
        var game = (await GameCatalog.ForZoneAsync(_db, device.ZoneId)).FirstOrDefault(g => g.Id == gameId);
        if (game == null) return GameErrors.NotFound;
        if (game.AccountPlatform is not { } platform) return AccountErrors.GameNeedsNone;

        // The PC has an account of this platform already: the same one for the same session
        var mine = await _db.GameAccountLeases.AsNoTracking().FirstOrDefaultAsync(l => l.DeviceId == deviceId && l.Platform == platform && l.EndedUtc == null);
        if (mine != null && mine.SessionId == session.Id)
        {
            var kept = await _db.GameAccounts.AsNoTracking().FirstAsync(a => a.Id == mine.AccountId);
            return BillingResult<DeviceLeaseView>.Success(new DeviceLeaseView(mine.Id, platform, kept.LoginMask, mine.DeliveredUtc.HasValue));
        }
        if (mine != null) await EndLeasesAsync(_db, l => l.Id == mine.Id, now, "session_end", null); // a lease of an old session

        var tried = new List<Guid>();
        for (var attempt = 0; attempt < LeaseAttempts; attempt++)
        {
            // Free accounts, the one used longest ago first (accounts are used evenly)
            var busy = await _db.GameAccountLeases.AsNoTracking().Where(l => l.EndedUtc == null).Select(l => l.AccountId).ToListAsync();
            busy.AddRange(tried);
            var candidates = await _db.GameAccounts.AsNoTracking()
                .Where(a => a.Platform == platform && a.IsActive && a.RemovedUtc == null && a.SecretCipher != null && !busy.Contains(a.Id))
                .Select(a => new { a.Id, a.LoginMask, a.CreatedUtc }).ToListAsync();
            if (candidates.Count == 0) break;
            var ids = candidates.Select(c => c.Id).ToList();
            var lastUse = (await _db.GameAccountLeases.AsNoTracking().Where(l => ids.Contains(l.AccountId)).Select(l => new { l.AccountId, l.Id }).ToListAsync())
                .GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Max(x => x.Id));
            var pick = candidates.OrderBy(c => lastUse.GetValueOrDefault(c.Id)).ThenBy(c => c.CreatedUtc).First();
            tried.Add(pick.Id);
            var lease = new GameAccountLeaseEntity
            {
                AccountId = pick.Id, DeviceId = deviceId, Platform = platform, SessionId = session.Id, UserId = session.UserId, GameId = gameId, StartedUtc = now
            };
            _db.GameAccountLeases.Add(lease);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Another PC took this account at the same moment (or this PC got one): try again
                _db.ChangeTracker.Clear();
                var other = await _db.GameAccountLeases.AsNoTracking()
                    .FirstOrDefaultAsync(l => l.DeviceId == deviceId && l.Platform == platform && l.EndedUtc == null && l.SessionId == session.Id);
                if (other != null)
                {
                    var mask = await _db.GameAccounts.AsNoTracking().Where(a => a.Id == other.AccountId).Select(a => a.LoginMask).FirstAsync();
                    return BillingResult<DeviceLeaseView>.Success(new DeviceLeaseView(other.Id, platform, mask, other.DeliveredUtc.HasValue));
                }
                continue;
            }
            var player = session.UserId is { } uid ? await _db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.Username).FirstOrDefaultAsync() : null;
            await AuditAsync(AuditActions.GameAccountLeased, pick.Id,
                $"Club account {platform} {pick.LoginMask} leased to {device.Hostname} for {(player != null ? "@" + player : "a guest")} ({game.Name})",
                new { platform, login = pick.LoginMask, lease = lease.Id, device = device.Hostname, sessionId = session.Id, player, game = game.Name });
            return BillingResult<DeviceLeaseView>.Success(new DeviceLeaseView(lease.Id, platform, pick.LoginMask, false));
        }
        var total = await _db.GameAccounts.AsNoTracking().CountAsync(a => a.Platform == platform && a.IsActive && a.RemovedUtc == null);
        return AccountErrors.NoFreeAccount.With(platform, total);
    }

    /// <summary>
    /// The credentials of a lease of this PC, once: only while the lease is open and its session still runs on the PC.
    /// The secret is read in memory and returned to the agent; it is never stored, logged or audited in clear text.
    /// </summary>
    public async Task<BillingResult<DeviceCredentialView>> DeliverAsync(Guid deviceId, long leaseId)
    {
        var now = _clock.UtcNow;
        var lease = await _db.GameAccountLeases.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leaseId && l.DeviceId == deviceId && l.EndedUtc == null);
        if (lease == null) return AccountErrors.LeaseNotFound;
        if (!await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == lease.SessionId && s.DeviceId == deviceId && s.Status == "active"))
        {
            await EndLeasesAsync(_db, l => l.Id == leaseId, now, "session_end", null);
            return AccountErrors.LeaseNotFound;
        }
        if (lease.DeliveredUtc != null) return AccountErrors.Delivered;
        var account = await _db.GameAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == lease.AccountId);
        if (account == null || account.RemovedUtc != null) return AccountErrors.LeaseNotFound;
        var login = _vault.TryUnprotect(account.LoginCipher);
        var secret = _vault.TryUnprotect(account.SecretCipher);
        if (login == null || secret == null) return AccountErrors.Unreadable;
        // Once per lease: a second request (or a second agent request at the same moment) gets 409
        var marked = await _db.GameAccountLeases.Where(l => l.Id == leaseId && l.DeliveredUtc == null && l.EndedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DeliveredUtc, now));
        if (marked != 1) return AccountErrors.Delivered;
        var host = await _db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";
        await AuditAsync(AuditActions.GameAccountDelivered, account.Id,
            $"Credentials of club account {Name(account)} handed to the agent of {host} (lease #{leaseId})",
            new { account.Platform, login = account.LoginMask, lease = leaseId, device = host });
        return BillingResult<DeviceCredentialView>.Success(new DeviceCredentialView(leaseId, account.Platform, login, secret));
    }

    // ===== The end of leases =====

    private static async Task<int> EndLeasesAsync(CmsDbContext db, System.Linq.Expressions.Expression<Func<GameAccountLeaseEntity, bool>> which,
        DateTimeOffset now, string reason, string? by, CancellationToken ct = default) =>
        await db.GameAccountLeases.Where(which).Where(l => l.EndedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.EndedUtc, now).SetProperty(l => l.EndReason, reason).SetProperty(l => l.EndedByUsername, by), ct);

    /// <summary>Ends the open leases of a session (its end, its expiry, the logout of the player); optionally only those of one PC.</summary>
    public static Task<int> EndForSessionAsync(CmsDbContext db, Guid sessionId, DateTimeOffset now, string reason, Guid? deviceId = null, CancellationToken ct = default) =>
        EndLeasesAsync(db, l => l.SessionId == sessionId && (deviceId == null || l.DeviceId == deviceId), now, reason, null, ct);

    /// <summary>
    /// Ends the open leases whose session is no longer the active session of their PC (it ended, it moved, the server was
    /// down): every 5 s and at the start of the server. Returns the number ended.
    /// </summary>
    public static async Task<int> EndStaleAsync(CmsDbContext db, DateTimeOffset now, string reason, CancellationToken ct = default)
    {
        var open = await db.GameAccountLeases.AsNoTracking().Where(l => l.EndedUtc == null).Select(l => new { l.Id, l.DeviceId, l.SessionId }).ToListAsync(ct);
        if (open.Count == 0) return 0;
        var sessionIds = open.Select(l => l.SessionId).Distinct().ToList();
        var live = (await db.Sessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id) && s.Status == "active").Select(s => new { s.Id, s.DeviceId }).ToListAsync(ct))
            .ToHashSet();
        var stale = open.Where(l => !live.Contains(new { Id = l.SessionId, l.DeviceId })).Select(l => l.Id).ToList();
        if (stale.Count == 0) return 0;
        return await EndLeasesAsync(db, l => stale.Contains(l.Id), now, reason, null, ct);
    }
}
