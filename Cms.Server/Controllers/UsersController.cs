using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.FrontDesk)]
public class UsersController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly LoginThrottle _throttle;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public UsersController(CmsDbContext db, LoginThrottle throttle, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _throttle = throttle;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserView>>> List()
    {
        // SQLite cannot ORDER BY DateTimeOffset — sort in memory
        var users = (await _db.Users.AsNoTracking().ToListAsync()).OrderByDescending(u => u.CreatedUtc).ToList();
        return Ok(users.Select(u => new UserView(u.Id, u.Username, u.DisplayName, u.Balance, u.BonusPoints, u.CreatedUtc, u.Debt)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserView>> Get(Guid id)
    {
        var u = await _db.Users.FindAsync(id);
        if (u == null) return NotFound();
        return Ok(new UserView(u.Id, u.Username, u.DisplayName, u.Balance, u.BonusPoints, u.CreatedUtc, u.Debt));
    }

    /// <summary>Staff create a player account. A password or PIN is required for the lock-screen login.</summary>
    [HttpPost("register")]
    public async Task<ActionResult<UserView>> Register([FromBody] RegisterUserRequest req)
    {
        var username = (req.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (username.Length is 0 or > 100)
            return BadRequest("Username is required (at most 100 characters)");
        var passwordError = Passwords.ValidatePlayerPassword(req.Password);
        if (passwordError != null) return BadRequest(passwordError);

        if (await _db.Users.AnyAsync(u => u.Username == username))
            return Conflict("Username already exists");

        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? username : req.DisplayName.Trim(),
            PasswordHash = Passwords.Hash(req.Password!),
            Balance = 0,
            BonusPoints = 0,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.PlayerRegistered, "player", user.Id, $"Player @{user.Username} registered", new { user.Username });
        return Ok(new UserView(user.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc, user.Debt));
    }

    /// <summary>
    /// Player login on the lock screen. A password or PIN is always required: an account without one
    /// cannot log in until staff set it (POST /api/users/{id}/password). Old unsalted SHA-256 hashes
    /// are replaced by a salted hash at the first successful login.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous] // player login on the lock screen (Cms.Launcher)
    [EnableRateLimiting(AuthConstants.AuthRateLimitPolicy)]
    public async Task<ActionResult<UserView>> Login([FromBody] LoginRequest req)
    {
        var username = (req.Username ?? string.Empty).Trim().ToLowerInvariant();
        var key = LoginThrottle.Key("player", username, HttpContext.ClientIp());
        var now = DateTimeOffset.UtcNow;
        if (_throttle.IsLockedOut(key, now))
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many failed attempts. Try again in 15 minutes.");

        var user = username.Length == 0 ? null : await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        var check = PasswordCheck.Failed;
        if (user?.PasswordHash != null)
            check = Passwords.Verify(user.PasswordHash, req.Password);
        else
            Passwords.VerifyDummy(req.Password); // same time for unknown users and users without a password

        if (check == PasswordCheck.Failed)
        {
            _throttle.RecordFailure(key, now);
            return Unauthorized("Invalid credentials");
        }

        _throttle.Reset(key);
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user!.PasswordHash = Passwords.Hash(req.Password!);
            await _db.SaveChangesAsync();
        }
        return Ok(new UserView(user!.Id, user.Username, user.DisplayName, user.Balance, user.BonusPoints, user.CreatedUtc, user.Debt));
    }

    /// <summary>Set or reset the password / PIN of a player (front desk).</summary>
    [HttpPost("{id:guid}/password")]
    public async Task<IActionResult> SetPassword(Guid id, [FromBody] SetPlayerPasswordRequest req)
    {
        var error = Passwords.ValidatePlayerPassword(req.Password);
        if (error != null) return BadRequest(error);
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();
        user.PasswordHash = Passwords.Hash(req.Password!);
        await _db.SaveChangesAsync();
        await _audit.WriteAsync(AuditActions.PlayerPasswordSet, "player", user.Id, $"Password / PIN of @{user.Username} set by staff");
        return NoContent();
    }

    private DateTimeOffset Now => _clock.UtcNow;
    private StaffActor? Actor => StaffActor.From(HttpContext?.User);

    private async Task<UserView> ViewAsync(Guid id)
    {
        var u = await _db.Users.AsNoTracking().FirstAsync(x => x.Id == id);
        return new UserView(u.Id, u.Username, u.DisplayName, u.Balance, u.BonusPoints, u.CreatedUtc, u.Debt);
    }

    /// <summary>
    /// Top-up at the desk in cash or by card (front desk). Cashback tiers (billing settings) add bonus
    /// in the same transaction; both are ledger rows.
    /// </summary>
    [HttpPost("{id:guid}/topup")]
    public async Task<ActionResult<UserView>> TopUp(Guid id, [FromBody] TopUpRequest req)
    {
        if (req.Amount <= 0 || req.Amount > 100_000 || req.Amount != TariffCalculator.Round(req.Amount))
            return BillingErrors.BadAmount.ToActionResult();
        var method = PaymentMethods.Normalize(req.Method) ?? PaymentMethods.Cash;
        if (!PaymentMethods.IsDesk(method)) return BillingErrors.BadPaymentMethod.ToActionResult();
        var tiers = await BonusRules.LoadAsync(_db);
        var bonus = BonusRules.CashbackFor(tiers, req.Amount);
        var now = Now;

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            if (!await Wallet.CreditAsync(_db, id, req.Amount)) return NotFound();
            var description = string.IsNullOrWhiteSpace(req.Description) ? $"Поповнення балансу +{req.Amount} ₴" : req.Description.Trim();
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = id, Type = LedgerTypes.TopUp, Method = method, Amount = req.Amount,
                BalanceDelta = req.Amount, DeskAmount = req.Amount, Description = description
            }, Actor, now);
            if (bonus > 0)
            {
                await Wallet.CreditAsync(_db, id, 0m, bonus);
                var tier = BonusRules.TierFor(tiers, req.Amount)!;
                await Ledger.AppendAsync(_db, new LedgerEntryEntity
                {
                    UserId = id, Type = LedgerTypes.BonusAccrual, Method = PaymentMethods.None, Amount = bonus, BonusDelta = bonus,
                    Description = $"Кешбек {tier.Percent:0.##}% за поповнення {req.Amount} ₴"
                }, Actor, now);
            }
            await _audit.WriteAsync(AuditActions.PlayerTopUp, "player", id,
                $"Balance top-up +{req.Amount} ₴ ({method})" + (bonus > 0 ? $", cashback +{bonus} ₴ bonus" : ""),
                new { amount = req.Amount, method, bonus, req.Description });
            await tx.CommitAsync();
        }
        return Ok(await ViewAsync(id));
    }

    /// <summary>
    /// Refund from the real balance in cash or by card (Owner, Manager), with a reason. Bonus is never
    /// paid out; the cashback of the refunded amount is taken back (at most the bonus left).
    /// </summary>
    [HttpPost("{id:guid}/refund")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<UserView>> Refund(Guid id, [FromBody] RefundRequest req)
    {
        if (req.Amount <= 0 || req.Amount > 100_000 || req.Amount != TariffCalculator.Round(req.Amount))
            return BillingErrors.BadAmount.ToActionResult();
        var reason = CleanReason(req.Reason);
        if (reason == null) return BillingErrors.ReasonRequired.ToActionResult();
        var method = PaymentMethods.Normalize(req.Method) ?? PaymentMethods.Cash;
        if (!PaymentMethods.IsDesk(method)) return BillingErrors.BadPaymentMethod.ToActionResult();
        var reversal = BonusRules.CashbackFor(await BonusRules.LoadAsync(_db), req.Amount);
        var now = Now;

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            if (!await Wallet.TryTakeAsync(_db, id, req.Amount, 0m))
            {
                if (!await _db.Users.AnyAsync(u => u.Id == id)) return NotFound();
                return BillingErrors.BalanceTooLow.ToActionResult();
            }
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = id, Type = LedgerTypes.Refund, Method = method, Amount = req.Amount,
                BalanceDelta = -req.Amount, DeskAmount = -req.Amount, Reason = reason,
                Description = $"Повернення {req.Amount} ₴ ({(method == PaymentMethods.Card ? "на картку" : "готівкою")}): {reason}"
            }, Actor, now);
            var taken = reversal > 0 ? await Wallet.TakeBonusUpToAsync(_db, id, reversal) : 0m;
            if (taken > 0)
            {
                await Ledger.AppendAsync(_db, new LedgerEntryEntity
                {
                    UserId = id, Type = LedgerTypes.BonusReversal, Method = PaymentMethods.None, Amount = taken, BonusDelta = -taken,
                    Reason = reason, Description = $"Скасування кешбеку за повернені {req.Amount} ₴"
                }, Actor, now);
            }
            await _audit.WriteAsync(AuditActions.PlayerRefund, "player", id,
                $"Refund {req.Amount} ₴ ({method}): {reason}" + (taken > 0 ? $"; cashback -{taken} ₴ bonus" : ""),
                new { amount = req.Amount, method, reason, bonusReversed = taken });
            await tx.CommitAsync();
        }
        return Ok(await ViewAsync(id));
    }

    /// <summary>
    /// Correction with a mandatory reason (Owner, Manager): positive or negative, of the real balance
    /// (default) or of the bonus balance (target = bonus). It cannot take a balance below zero.
    /// </summary>
    [HttpPost("{id:guid}/adjust")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<UserView>> Adjust(Guid id, [FromBody] AdjustBalanceRequest req)
    {
        if (req.Amount == 0 || Math.Abs(req.Amount) > 100_000 || req.Amount != TariffCalculator.Round(req.Amount))
            return BadRequest(new { error = "Amount must not be 0 (at most 100000)", code = "bad_amount" });
        var reason = CleanReason(req.Reason);
        if (reason == null) return BillingErrors.ReasonRequired.ToActionResult();
        var bonus = string.Equals(req.Target?.Trim(), "bonus", StringComparison.OrdinalIgnoreCase);
        if (!bonus && !string.IsNullOrWhiteSpace(req.Target) && !string.Equals(req.Target.Trim(), "balance", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Target must be balance or bonus", code = "bad_target" });
        var amount = Math.Abs(req.Amount);
        var now = Now;

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var ok = req.Amount > 0
                ? await Wallet.CreditAsync(_db, id, bonus ? 0m : amount, bonus ? amount : 0m)
                : await Wallet.TryTakeAsync(_db, id, bonus ? 0m : amount, bonus ? amount : 0m);
            if (!ok)
            {
                if (!await _db.Users.AnyAsync(u => u.Id == id)) return NotFound();
                return BillingErrors.BalanceTooLow.ToActionResult();
            }
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = id, Type = LedgerTypes.Adjustment, Method = PaymentMethods.None, Amount = amount,
                BalanceDelta = bonus ? 0m : req.Amount, BonusDelta = bonus ? req.Amount : 0m, Reason = reason,
                Description = $"Коригування {(bonus ? "бонусів" : "балансу")} {req.Amount:+0.##;-0.##} ₴: {reason}"
            }, Actor, now);
            await _audit.WriteAsync(bonus ? AuditActions.PlayerBonusAdjusted : AuditActions.PlayerBalanceAdjusted, "player", id,
                $"{(bonus ? "Bonus" : "Balance")} correction {req.Amount:+0.##;-0.##} ₴: {reason}", new { amount = req.Amount, target = bonus ? "bonus" : "balance", reason });
            await tx.CommitAsync();
        }
        return Ok(await ViewAsync(id));
    }

    /// <summary>
    /// The player pays off (part of) the debt: cash or card at the desk (needs an open shift) or from
    /// the real balance. Never more than the debt.
    /// </summary>
    [HttpPost("{id:guid}/debt/pay")]
    public async Task<ActionResult<UserView>> PayDebt(Guid id, [FromBody] DebtPaymentRequest req)
    {
        if (req.Amount <= 0 || req.Amount > 100_000 || req.Amount != TariffCalculator.Round(req.Amount))
            return BillingErrors.BadAmount.ToActionResult();
        var method = PaymentMethods.Normalize(req.Method) ?? PaymentMethods.Cash;
        if (method == string.Empty) return BillingErrors.BadPaymentMethod.ToActionResult();
        var fromBalance = method == PaymentMethods.Balance;
        var now = Now;

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            if (!await Wallet.TryReduceDebtAsync(_db, id, req.Amount, fromBalance))
            {
                var have = await Wallet.GetAccountsAsync(_db, id);
                if (have == null) return NotFound();
                return (have.Debt < req.Amount ? BillingErrors.DebtTooHigh.With(have.Debt) : BillingErrors.BalanceTooLow).ToActionResult();
            }
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = id, Type = LedgerTypes.DebtPayment, Method = method, Amount = req.Amount, DebtDelta = -req.Amount,
                BalanceDelta = fromBalance ? -req.Amount : 0m, DeskAmount = fromBalance ? 0m : req.Amount,
                Description = $"Сплата боргу {req.Amount} ₴ ({(fromBalance ? "з балансу" : method == PaymentMethods.Card ? "карткою" : "готівкою")})"
            }, Actor, now);
            var left = await Wallet.GetAccountsAsync(_db, id);
            await _audit.WriteAsync(AuditActions.PlayerDebtPaid, "player", id, $"Debt paid: {req.Amount} ₴ ({method}), {left?.Debt} ₴ left",
                new { amount = req.Amount, method, debtLeft = left?.Debt });
            await tx.CommitAsync();
        }
        return Ok(await ViewAsync(id));
    }

    /// <summary>Writes off (part of) the debt without money (Owner, Manager), with a reason.</summary>
    [HttpPost("{id:guid}/debt/writeoff")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<UserView>> WriteOffDebt(Guid id, [FromBody] DebtWriteOffRequest req)
    {
        if (req.Amount <= 0 || req.Amount > 100_000 || req.Amount != TariffCalculator.Round(req.Amount))
            return BillingErrors.BadAmount.ToActionResult();
        var reason = CleanReason(req.Reason);
        if (reason == null) return BillingErrors.ReasonRequired.ToActionResult();
        var now = Now;

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            if (!await Wallet.TryReduceDebtAsync(_db, id, req.Amount, fromBalance: false))
            {
                var have = await Wallet.GetAccountsAsync(_db, id);
                if (have == null) return NotFound();
                return BillingErrors.DebtTooHigh.With(have.Debt).ToActionResult();
            }
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = id, Type = LedgerTypes.DebtWriteOff, Method = PaymentMethods.None, Amount = req.Amount, DebtDelta = -req.Amount,
                Reason = reason, Description = $"Списання боргу {req.Amount} ₴: {reason}"
            }, Actor, now);
            await _audit.WriteAsync(AuditActions.PlayerDebtWrittenOff, "player", id, $"Debt written off: {req.Amount} ₴: {reason}",
                new { amount = req.Amount, reason });
            await tx.CommitAsync();
        }
        return Ok(await ViewAsync(id));
    }

    private static string? CleanReason(string? reason)
    {
        var r = reason?.Trim();
        if (string.IsNullOrEmpty(r) || r.Length < 3) return null;
        return r.Length > 300 ? r[..300] : r;
    }

    /// <summary>Ledger rows of a player, newest first (front desk: player card).</summary>
    [HttpGet("{id:guid}/ledger")]
    public async Task<ActionResult<IEnumerable<LedgerEntryView>>> LedgerHistory(Guid id, [FromQuery] int take = 100, [FromQuery] long? beforeId = null)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == id)) return NotFound();
        var rows = await _db.Ledger.AsNoTracking()
            .Where(l => l.UserId == id && (beforeId == null || l.Id < beforeId))
            .OrderByDescending(l => l.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync();
        return Ok(rows.Select(LedgerEntryView.From));
    }

    /// <summary>Cached balances against the sum of the player's ledger rows.</summary>
    [HttpGet("{id:guid}/ledger/check")]
    public async Task<ActionResult<LedgerCheckView>> LedgerCheck(Guid id)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var mismatch = (await Ledger.ReconcileAsync(_db, id)).SingleOrDefault();
        var broken = await Ledger.CheckChainAsync(_db, id);
        return Ok(new LedgerCheckView(user.Balance, mismatch?.LedgerBalance ?? user.Balance,
            user.BonusPoints, mismatch?.LedgerBonus ?? user.BonusPoints, mismatch == null && broken == null, broken)
        {
            Debt = user.Debt,
            LedgerDebt = mismatch?.LedgerDebt ?? user.Debt
        });
    }

    /// <summary>Balance history in the format of earlier versions (now read from the ledger).</summary>
    [HttpGet("{id:guid}/transactions")]
    public async Task<ActionResult<IEnumerable<TransactionView>>> Transactions(Guid id)
    {
        var rows = await _db.Ledger.AsNoTracking()
            .Where(l => l.UserId == id)
            .OrderByDescending(l => l.Id)
            .Take(100)
            .ToListAsync();
        return Ok(rows.Select(t => new TransactionView(Guid.Empty, id, t.BalanceDelta + t.BonusDelta, t.Type, t.Description, t.CreatedUtc)));
    }
}

public record RegisterUserRequest(string? Username, string? Password, string? DisplayName);
public record LoginRequest(string? Username, string? Password);
public record SetPlayerPasswordRequest(string? Password);
/// <summary>Method: cash (default) or card.</summary>
public record TopUpRequest(decimal Amount, string? Description, string? Method = null);
/// <summary>Target: balance (default) or bonus.</summary>
public record AdjustBalanceRequest(decimal Amount, string? Reason, string? Target = null);
/// <summary>Method: cash (default) or card.</summary>
public record RefundRequest(decimal Amount, string? Method, string? Reason);
/// <summary>Ok: cached balances (and debt) equal the ledger sums and every BalanceAfter follows from the previous row (else BrokenRowId).</summary>
public record LedgerCheckView(decimal Balance, decimal LedgerBalance, decimal Bonus, decimal LedgerBonus, bool Ok, long? BrokenRowId = null)
{
    public decimal Debt { get; init; }
    public decimal LedgerDebt { get; init; }
}
/// <summary>Method: cash (default), card or balance (the player's real balance).</summary>
public record DebtPaymentRequest(decimal Amount, string? Method = null);
public record DebtWriteOffRequest(decimal Amount, string? Reason);

public record LedgerEntryView(
    long Id, DateTimeOffset CreatedUtc, Guid? UserId, string Type, string Method, decimal Amount,
    decimal BalanceDelta, decimal BonusDelta, decimal DeskAmount, decimal? BalanceAfter, decimal? BonusAfter,
    Guid? SessionId, Guid? PackageId, Guid? DeviceId, string? Description, string? Reason, string? ActorUsername)
{
    /// <summary>Shift that was open when the row was written.</summary>
    public long? ShiftId { get; init; }
    /// <summary>Change of the player's debt (+ recorded, - paid or written off) and the debt after the row.</summary>
    public decimal DebtDelta { get; init; }
    public decimal? DebtAfter { get; init; }
    /// <summary>Bar receipt of a bar sale or refund.</summary>
    public long? SaleId { get; init; }

    public static LedgerEntryView From(LedgerEntryEntity l) => new(
        l.Id, l.CreatedUtc, l.UserId, l.Type, l.Method, l.Amount, l.BalanceDelta, l.BonusDelta, l.DeskAmount,
        l.BalanceAfter, l.BonusAfter, l.SessionId, l.PackageId, l.DeviceId, l.Description, l.Reason, l.ActorUsername)
    {
        ShiftId = l.ShiftId,
        SaleId = l.SaleId,
        DebtDelta = l.DebtDelta,
        DebtAfter = l.DebtAfter
    };
}
/// <summary>Debt: unpaid post-paid time on the player account (0 = none).</summary>
public record UserView(Guid Id, string Username, string DisplayName, decimal Balance, decimal BonusPoints, DateTimeOffset CreatedUtc, decimal Debt = 0);
public record TransactionView(Guid Id, Guid UserId, decimal Amount, string Type, string? Description, DateTimeOffset CreatedUtc);
