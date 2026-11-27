using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Devices;
using Cms.Server.Repositories;
using Cms.Server.Reservations;
using Cms.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Player login on the lock screen (spec area 1). Only the agent of an approved PC calls these
/// endpoints, with its device key, for its own PC; the lock screen talks to the agent through a local
/// pipe. A login checks the user name and the PIN or password of a registered player and starts a
/// session on this PC with the existing billing rules, paid from the player's balances:
///   offer "balance"  - open-ended, ends when the balances are used up (the default);
///   offer "minutes"  - prepaid tariff minutes;
///   offer "package"  - a package of the zone that is on sale now.
/// Wrong names and wrong secrets get the same answer; lockout per account and per PC (PlayerLoginLimiter).
/// A session started by the cashier at the desk works as before.
/// </summary>
[ApiController]
[Route("api/devices/{deviceId:guid}")]
[Authorize(Policy = Policies.ApprovedDevice)]
public class PlayerLoginController : ControllerBase
{
    public const string OfferBalance = "balance";
    public const string OfferMinutes = "minutes";
    public const string OfferPackage = "package";
    /// <summary>Prepaid choices on the lock screen.</summary>
    public static readonly int[] MinuteChoices = { 60, 120, 180 };

    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly AuditLogger _audit;
    private readonly PlayerLoginLimiter _limiter;
    private readonly ClubClock _clock;

    public PlayerLoginController(CmsDbContext db, IDeviceRepository devices, AuditLogger audit, PlayerLoginLimiter limiter, ClubClock? clock = null)
    {
        _db = db;
        _devices = devices;
        _audit = audit;
        _limiter = limiter;
        _clock = clock ?? ClubClock.System();
    }

    private static readonly BillingError LoginFailed = new("login_failed", "Wrong user name or PIN / password.", StatusCodes.Status401Unauthorized);
    private static readonly BillingError LoginLocked = new("login_locked", "Too many failed logins. Try again in {0} min.", StatusCodes.Status429TooManyRequests);
    private static readonly BillingError BadOffer = new("bad_offer", "Offer must be balance, minutes (60, 120, 180) or package.");

    private async Task<DeviceEntity?> DeviceAsync(Guid deviceId) =>
        User.DeviceId() != deviceId ? null : await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId);

    /// <summary>What a player can buy on this PC now: the zone tariff, prepaid minutes and the packages on sale.</summary>
    [HttpGet("login-offers")]
    public async Task<ActionResult<LoginOffersView>> Offers(Guid deviceId)
    {
        var device = await DeviceAsync(deviceId);
        if (device == null) return Forbid();
        var now = _clock.UtcNow;
        var currency = await _db.CurrencyAsync();
        var tariff = await SessionService.ZoneTariffAsync(_db, device.ZoneId);
        TariffOfferView? tariffView = null;
        var minutes = new List<MinutesOfferView>();
        if (tariff != null)
        {
            var pricing = TariffPricing.From(tariff, await _db.TariffRules.AsNoTracking().Where(r => r.TariffId == tariff.Id).ToListAsync());
            var (priceNow, rule) = TariffCalculator.PriceAt(pricing, _clock.ToLocal(now));
            tariffView = new TariffOfferView(tariff.Id, tariff.Name, priceNow, rule);
            minutes.AddRange(MinuteChoices.Select(m => new MinutesOfferView(m, TariffCalculator.QuoteFixed(pricing, now, m, _clock.Zone).Total)));
        }
        var packages = device.ZoneId == null ? new List<PackageOfferView>() :
            (await _db.Packages.AsNoTracking().Where(p => p.ZoneId == device.ZoneId && p.IsActive).ToListAsync())
            .Select(p => (Package: p, Offer: PackageWindows.Evaluate(p, now, _clock.Zone)))
            .Where(x => x.Offer.Available)
            .OrderBy(x => x.Package.SortOrder).ThenBy(x => x.Package.Name)
            .Select(x => new PackageOfferView(x.Package.Id, x.Package.Name, x.Package.Price, x.Offer.Terms!.EndUtc, x.Offer.Terms.Minutes))
            .ToList();
        return Ok(new LoginOffersView(currency.Code, currency.Symbol, tariffView, minutes, packages));
    }

    [HttpPost("player-login")]
    public async Task<IActionResult> Login(Guid deviceId, [FromBody] PlayerLoginRequest req)
    {
        var device = await DeviceAsync(deviceId);
        if (device == null) return Forbid();
        var now = _clock.UtcNow;
        var username = (req.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (username.Length > 100) username = username[..100];

        var offer = (req.Offer ?? OfferBalance).Trim().ToLowerInvariant();
        if (offer is not (OfferBalance or OfferMinutes or OfferPackage) ||
            (offer == OfferMinutes && !MinuteChoices.Contains(req.Minutes ?? 0)) ||
            (offer == OfferPackage && (req.PackageId is null || req.PackageId == Guid.Empty)))
            return BadOffer.ToActionResult();

        // The PC must be free; this says nothing about any account
        var sessions = await _db.Sessions.AsNoTracking().Where(s => s.DeviceId == deviceId && s.Status == "active").ToListAsync();
        if (sessions.Any(s => !SessionService.IsDue(s, now))) return BillingErrors.DeviceBusy.ToActionResult();

        if (_limiter.LockedFor(deviceId, username, now) is { } wait)
            return LoginLocked.With((int)Math.Ceiling(wait.TotalMinutes)).ToActionResult();

        var user = username.Length == 0 ? null : await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        var check = PasswordCheck.Failed;
        if (user?.PasswordHash != null)
            check = Passwords.Verify(user.PasswordHash, req.Secret);
        else
            Passwords.VerifyDummy(req.Secret); // the same time for unknown names and accounts without a secret

        if (check == PasswordCheck.Failed)
        {
            var (accountLocked, deviceLocked) = _limiter.RecordFailure(deviceId, username, now);
            // A name that is no account is not written down: players type their password into the name field
            var who = user != null ? $"@{user.Username}" : "an unknown user name";
            await _audit.WriteAsync(AuditActions.PlayerLoginFailed, "device", deviceId,
                $"Failed player login on {device.Hostname} for {who}", new { device.Hostname, player = user?.Username });
            if (accountLocked || deviceLocked)
                await _audit.WriteAsync(AuditActions.PlayerLoginLocked, "device", deviceId,
                    $"Player login locked for {PlayerLoginLimiter.Lockout.TotalMinutes:0} min: " +
                    (accountLocked ? (user != null ? $"account @{user.Username}" : "an unknown user name") : "") +
                    (accountLocked && deviceLocked ? " and " : "") + (deviceLocked ? $"PC {device.Hostname}" : ""),
                    new { device.Hostname, player = user?.Username, account = accountLocked, device = deviceLocked });
            return LoginFailed.ToActionResult();
        }

        _limiter.RecordSuccess(deviceId, username);
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user!.PasswordHash = Passwords.Hash(req.Secret!);
            await _db.SaveChangesAsync();
        }

        // A player plays on one PC at a time (sessions from the balance would spend the same money)
        var elsewhere = await _db.Sessions.AsNoTracking().Where(s => s.UserId == user!.Id && s.Status == "active" && s.DeviceId != deviceId)
            .Select(s => s.DeviceId).FirstOrDefaultAsync();
        if (elsewhere != Guid.Empty)
        {
            var host = await _db.Devices.AsNoTracking().Where(d => d.Id == elsewhere).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";
            await _audit.WriteAsync(AuditActions.PlayerLogin, "player", user!.Id,
                $"@{user.Username} logged in on {device.Hostname}, no session: player_busy (plays on {host})", new { device.Hostname, offer, error = "player_busy", playsOn = host });
            return BillingErrors.PlayerBusy.With(host).ToActionResult();
        }

        // A PC held for a reservation: the reserved player checks in with the login; everybody else is refused
        var hold = await ReservationHolds.ActiveAsync(_db, deviceId, _clock);
        if (hold != null && hold.UserId != user!.Id)
        {
            await _audit.WriteAsync(AuditActions.PlayerLogin, "player", user.Id,
                $"@{user.Username} logged in on {device.Hostname}, no session: pc_reserved (reservation #{hold.Id} at {hold.StartLocal})",
                new { device.Hostname, offer, error = "pc_reserved", reservation = hold.Id });
            return ReservationErrors.PcReserved.With(hold.Name, hold.StartLocal, hold.Id).ToActionResult();
        }

        BillingResult<SessionEntity> start;
        if (hold != null)
        {
            var mode = offer switch { OfferBalance => "balance", OfferPackage => "package", _ => "minutes" };
            var checkIn = await new ReservationService(_db, _devices, _clock, _audit).CheckInAsync(hold.Id,
                new CheckInRequest(deviceId, mode, offer == OfferMinutes ? req.Minutes : null, offer == OfferPackage ? req.PackageId : null, PaymentMethods.Balance));
            start = checkIn.Ok ? BillingResult<SessionEntity>.Success(checkIn.Value.Session) : checkIn.Error!;
        }
        else
        {
            var service = new SessionService(_db, _devices, _clock, _audit);
            start = await service.StartAsync(new StartSessionRequest(deviceId, null, user!.Id,
                offer == OfferMinutes ? req.Minutes : null, offer == OfferPackage ? req.PackageId : null,
                PaymentMethods.Balance, UseBonus: true, LimitToBalance: offer == OfferBalance));
        }
        if (!start.Ok)
        {
            await _audit.WriteAsync(AuditActions.PlayerLogin, "player", user!.Id,
                $"@{user.Username} logged in on {device.Hostname}, no session: {start.Error!.Code}", new { device.Hostname, offer, error = start.Error.Code });
            return start.Error.ToActionResult();
        }
        var session = start.Value!;
        await _audit.WriteAsync(AuditActions.PlayerLogin, "player", user!.Id,
            $"@{user.Username} logged in on {device.Hostname} and started a session ({offer})" + (hold != null ? $" of reservation #{hold.Id}" : ""),
            new { device.Hostname, offer, sessionId = session.Id, session.EndUtc, session.BalanceEndUtc, reservation = hold?.Id });
        var accounts = await Wallet.GetAccountsAsync(_db, user.Id);
        return Ok(new PlayerLoginView(session.Id, user.Username, user.DisplayName, accounts?.Balance ?? 0m, accounts?.Bonus ?? 0m,
            session.EndUtc ?? session.BalanceEndUtc, session.LimitToBalance, offer));
    }

    /// <summary>The player ends the session of a registered player on this PC (paid from the balances; a rest becomes debt).</summary>
    [HttpPost("player-logout")]
    public async Task<IActionResult> Logout(Guid deviceId)
    {
        var device = await DeviceAsync(deviceId);
        if (device == null) return Forbid();
        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.DeviceId == deviceId && s.Status == "active");
        if (session == null) return BillingErrors.SessionNotFound.ToActionResult();
        if (!session.UserId.HasValue) return NoPlayer.ToActionResult();
        var service = new SessionService(_db, _devices, _clock, _audit);
        var result = await service.EndAsync(session.Id, new EndSessionRequest(PaymentMethods.Balance, true, PaymentMethods.Debt), "ended by the player on the lock screen");
        if (!result.Ok) return result.Error!.ToActionResult();
        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == session.UserId);
        await _audit.WriteAsync(AuditActions.PlayerLogout, "player", user.Id,
            $"@{user.Username} ended the session on {device.Hostname}: {result.Value!.Session.TotalCost} {await _db.CurrencySymbolAsync()}",
            new { device.Hostname, sessionId = session.Id, cost = result.Value.Session.TotalCost, debt = result.Value.Debt });
        return Ok(new { sessionId = session.Id, cost = result.Value.Session.TotalCost, debt = result.Value.Debt });
    }

    private static readonly BillingError NoPlayer = new("session_no_player", "Only a session of a registered player can be ended on the PC.", StatusCodes.Status409Conflict);
}

/// <summary>Offer: balance (default), minutes (60, 120, 180) or package (PackageId).</summary>
public record PlayerLoginRequest(string? Username, string? Secret, string? Offer = null, int? Minutes = null, Guid? PackageId = null);
public record PlayerLoginView(Guid SessionId, string Username, string DisplayName, decimal Balance, decimal Bonus, DateTimeOffset? EndUtc, bool FromBalance, string Offer);
public record TariffOfferView(Guid Id, string Name, decimal PricePerHourNow, string? RuleNow);
public record MinutesOfferView(int Minutes, decimal Price);
public record PackageOfferView(Guid Id, string Name, decimal Price, DateTimeOffset EndsAtUtc, int Minutes);
public record LoginOffersView(string Currency, string CurrencySymbol, TariffOfferView? Tariff, IReadOnlyList<MinutesOfferView> Minutes, IReadOnlyList<PackageOfferView> Packages);
