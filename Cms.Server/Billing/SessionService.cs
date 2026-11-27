using Cms.Server.Auth;
using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Billing;

/// <summary>AmountDue: paid at the desk now; Debt: recorded on the player account instead (deskMethod debt).</summary>
public sealed record EndResult(SessionEntity Session, decimal AmountDue, Charge FromBalances, decimal Debt = 0);

/// <summary>Price difference of a move to another PC (see SessionService.QuoteTransferAsync).</summary>
public sealed record TransferQuote(
    Guid SessionId, Guid TargetDeviceId, string TargetHostname, Guid? TargetZoneId,
    Guid? TargetTariffId, string? TargetTariffName, Guid? TargetPackageId, string? TargetPackageName,
    decimal Surcharge, int RemainingMinutes, string Basis);

/// <summary>
/// Session rules (spec area 2) used by the API and the demo simulator:
///  - start with a tariff (prepaid minutes or open-ended) or a package of the PC's zone, paid from the
///    player balances (bonus first) or at the desk (cash/card; walk-in guests always at the desk);
///  - open-ended sessions are billed at the end per segment and per time-of-day rule;
///  - pause stops the time (PC locked); resume moves the end by the paused time, never past a cut-off;
///  - transfer moves an active session to a free PC; a move to another zone pays the price difference
///    for the remaining time (tariff time: price of the remaining window in both tariffs; package: the
///    pro-rated price difference to the package of the same kind in the target zone). A move to a
///    cheaper zone refunds nothing.
/// Money changes, ledger rows, the session row and the audit entry commit in one transaction; the
/// database allows one active session per PC and refuses concurrent changes of a session.
/// </summary>
public sealed class SessionService
{
    private readonly CmsDbContext _db;
    private readonly IDeviceRepository _devices;
    private readonly ClubClock _clock;
    private readonly AuditLogger? _audit;
    private readonly StaffActor? _actor;

    public SessionService(CmsDbContext db, IDeviceRepository devices, ClubClock? clock = null, AuditLogger? audit = null, StaffActor? actor = null)
    {
        _db = db;
        _devices = devices;
        _clock = clock ?? ClubClock.System();
        _audit = audit;
        _actor = actor;
    }

    /// <summary>Owner or Manager: may give free PC time (payment method free, with a reason).</summary>
    public bool CanGiveFreeTime { get; init; }

    private Task AuditAsync(string action, Guid sessionId, string summary, object details) =>
        _audit?.WriteAsync(action, "session", sessionId, summary, details) ?? Task.CompletedTask;

    private static bool IsFree(string? method) => string.Equals(method?.Trim(), PaymentMethods.Free, StringComparison.OrdinalIgnoreCase);

    /// <summary>Free time: Owner or Manager, with a reason (at least 3 characters).</summary>
    private BillingResult<string> CheckFree(string? reason)
    {
        if (!CanGiveFreeTime) return BillingErrors.FreeNeedsManager;
        var why = reason?.Trim();
        if (why is not { Length: >= 3 }) return BillingErrors.ReasonRequired;
        return BillingResult<string>.Success(why.Length > 300 ? why[..300] : why);
    }

    // ===== Start =====

    public async Task<BillingResult<SessionEntity>> StartAsync(StartSessionRequest req)
    {
        var cur = await _db.CurrencySymbolAsync();
        var device = await _db.Devices.FindAsync(req.DeviceId);
        if (device == null) return BillingErrors.DeviceNotFound;
        if (device.Status != DeviceStatuses.Approved) return BillingErrors.DeviceNotApproved;
        if (req.DurationMinutes is < 0 or > 24 * 60) return BillingErrors.BadDuration;

        UserEntity? user = null;
        if (req.UserId.HasValue)
        {
            user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId.Value);
            if (user == null) return BillingErrors.UserNotFound;
        }

        var now = _clock.UtcNow;
        PackageEntity? package = null;
        TariffEntity? tariff;
        PackageTerms? terms = null;
        decimal price;
        int? minutes = null;

        if (req.PackageId.HasValue)
        {
            package = await _db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.PackageId.Value);
            if (package == null) return BillingErrors.PackageNotFound;
            if (package.ZoneId == null || package.ZoneId != device.ZoneId) return BillingErrors.PackageOtherZone;
            var availability = PackageWindows.Evaluate(package, now, _clock.Zone);
            if (!availability.Available) return availability.Error!;
            terms = availability.Terms!;
            price = package.Price;
            minutes = terms.Minutes;
            // Zone tariff: prices extensions and transfers of the package session
            tariff = await ZoneTariffAsync(_db, device.ZoneId);
        }
        else
        {
            if (req.TariffId.HasValue)
            {
                tariff = await _db.Tariffs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == req.TariffId.Value);
                if (tariff == null) return BillingErrors.TariffNotFound;
                if (tariff.ZoneId.HasValue && tariff.ZoneId != device.ZoneId) return BillingErrors.TariffOtherZone;
            }
            else
            {
                tariff = await ZoneTariffAsync(_db, device.ZoneId);
            }
            if (tariff == null) return BillingErrors.NoTariff;
            minutes = req.DurationMinutes is > 0 ? req.DurationMinutes : null;
            price = minutes.HasValue
                ? TariffCalculator.QuoteFixed(await PricingAsync(tariff), now, minutes.Value, _clock.Zone).Total
                : 0m;
        }

        var prepaid = package != null || minutes.HasValue;
        // A player who owes money plays prepaid until the debt is paid (an open-ended session could add to it)
        if (!prepaid && user is { Debt: > 0 }) return BillingErrors.PlayerHasDebt.With(user.Debt);
        // "From the balance": open-ended, paid from the balances, ends when they are used up
        DateTimeOffset? balanceEnd = null;
        if (req.LimitToBalance)
        {
            if (prepaid || user == null || IsFree(req.PaymentMethod) ||
                (PaymentMethods.Normalize(req.PaymentMethod) is { } m && m != PaymentMethods.Balance)) return BillingErrors.BalanceLimitNeedsPlayer;
            // Two sessions would spend the same balances: one session from the balance per player
            var other = await _db.Sessions.AsNoTracking().Where(s => s.UserId == user.Id && s.Status == "active" && s.DeviceId != device.Id)
                .Select(s => s.DeviceId).FirstOrDefaultAsync();
            if (other != Guid.Empty)
                return BillingErrors.PlayerBusy.With(await _db.Devices.AsNoTracking().Where(d => d.Id == other).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?");
            var pricing = await PricingAsync(tariff!);
            var available = user.Balance + (req.UseBonus ? user.BonusPoints : 0m);
            balanceEnd = ProjectEnd(until => TariffCalculator.QuotePlayed(new[] { (pricing, now, until) }, pricing, _clock.Zone).Total, available, now);
            if (balanceEnd < now.AddMinutes(MinBalanceMinutes))
                return BillingErrors.InsufficientBalance.With(TariffCalculator.QuotePlayed(new[] { (pricing, now, now.AddMinutes(MinBalanceMinutes)) }, pricing, _clock.Zone).Total,
                    user.Balance, user.BonusPoints);
        }
        string? method = null;
        string? freeReason = null;
        var value = price;
        if (IsFree(req.PaymentMethod))
        {
            if (!prepaid) return BillingErrors.FreeNeedsPrepaid;
            var free = CheckFree(req.Reason);
            if (!free.Ok) return free.Error!;
            freeReason = free.Value;
            method = PaymentMethods.Free;
            price = 0m;
        }
        else if (prepaid)
        {
            var resolved = ResolveMethod(req.PaymentMethod, user);
            if (!resolved.Ok) return resolved.Error!;
            method = resolved.Value;
        }

        var session = new SessionEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            TariffId = tariff?.Id,
            UserId = user?.Id,
            StartUtc = now,
            EndUtc = terms?.EndUtc ?? (minutes.HasValue ? now.AddMinutes(minutes.Value) : null),
            CutoffUtc = terms?.CutoffUtc,
            Status = "active",
            TotalCost = price,
            IsPrepaid = prepaid,
            PackageId = package?.Id,
            PaymentMethod = method,
            PaidMinutes = minutes,
            LimitToBalance = balanceEnd.HasValue,
            BalanceEndUtc = balanceEnd
        };

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            // A prepaid session that has run out but is not closed yet does not block the PC
            var active = await _db.Sessions.Where(s => s.DeviceId == device.Id && s.Status == "active").ToListAsync();
            var expired = active.Where(s => IsDue(s, now)).ToList();
            if (active.Count > expired.Count) return BillingErrors.DeviceBusy;

            try
            {
                if (expired.Count > 0)
                {
                    await CloseAsync(expired, now);
                    await _db.SaveChangesAsync(); // free the unique index first
                }

                if (price > 0)
                {
                    var paid = await PayAsync(user, method!, price, req.UseBonus,
                        package != null ? LedgerTypes.PackagePurchase : LedgerTypes.SessionCharge, session, now,
                        package != null
                            ? $"Пакет «{package.Name}» на {device.Hostname} — {price} {cur}"
                            : $"Сесія на {device.Hostname} ({minutes} хв, {tariff!.Name}) — {price} {cur}");
                    if (!paid.Ok) return paid.Error!;
                }

                _db.Sessions.Add(session);
                _db.SessionSegments.Add(NewSegment(session, now));
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Another session was started on this PC at the same moment; the payment is rolled back
                return BillingErrors.DeviceBusy;
            }

            var what = package != null ? $"package '{package.Name}' until {_clock.ToLocal(session.EndUtc!.Value):HH:mm}"
                : prepaid ? $"{minutes} min"
                : balanceEnd.HasValue ? $"from the balance, until about {_clock.ToLocal(balanceEnd.Value):HH:mm}" : "open-ended";
            await AuditAsync(AuditActions.SessionStarted, session.Id,
                $"Session on {device.Hostname} for {(user != null ? "@" + user.Username : "a guest")}: {what}" +
                (price > 0 ? $", {price} {cur} " + PaidText(method) : ""),
                new
                {
                    device.Hostname, deviceId = device.Id, tariff = tariff?.Name, package = package?.Name, minutes, cost = price,
                    prepaid, player = user?.Username, paymentMethod = method, endUtc = session.EndUtc, cutoffUtc = session.CutoffUtc,
                    limitToBalance = session.LimitToBalance, balanceEndUtc = session.BalanceEndUtc
                });
            if (prepaid && price == 0)
            {
                // Time without payment: a free session with a reason, or a tariff / package that costs 0
                await AuditAsync(AuditActions.SessionFreeTime, session.Id,
                    $"Free time on {device.Hostname}: {what}, worth {value} {cur}" + (freeReason != null ? $": {freeReason}" : $" (price 0 {cur})"),
                    new { device.Hostname, minutes, package = package?.Name, value, reason = freeReason, player = user?.Username });
            }
            await tx.CommitAsync();
        }

        // Agent: end time (null = open-ended; from the balance: when it runs out), then unlock
        _devices.EnqueueCommand(device.Id, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = session.EndUtc ?? session.BalanceEndUtc }));
        _devices.EnqueueCommand(device.Id, new EnqueueCommandRequest(CommandTypes.Unlock, null));
        return BillingResult<SessionEntity>.Success(session);
    }

    // ===== End =====

    /// <summary>
    /// Ends a session. Open-ended sessions are billed now: from the player balances (bonus first,
    /// never below zero), the rest is due at the desk (paid in cash or by card, also written to the
    /// ledger). Prepaid time and packages were paid at the start; unused time is not refunded.
    /// </summary>
    public async Task<BillingResult<EndResult>> EndAsync(Guid id, EndSessionRequest? req = null, string? why = null)
    {
        var cur = await _db.CurrencySymbolAsync();
        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return BillingErrors.SessionNotFound;
        if (session.Status == "ended") return BillingErrors.SessionEnded;
        // balance (default for players): balances first, the rest at the desk with DeskMethod (default cash)
        // or, with DeskMethod debt, on the player's account; cash / card: everything at the desk (always for guests)
        var method = PaymentMethods.Normalize(req?.PaymentMethod);
        var owes = string.Equals(req?.DeskMethod?.Trim(), PaymentMethods.Debt, StringComparison.OrdinalIgnoreCase);
        var restMethod = owes ? PaymentMethods.Debt : PaymentMethods.Normalize(req?.DeskMethod);
        if (method == string.Empty || restMethod == string.Empty || restMethod == PaymentMethods.Balance) return BillingErrors.BadPaymentMethod;
        if (owes && !session.UserId.HasValue) return BillingErrors.DebtNeedsPlayer;
        if (owes && method is PaymentMethods.Cash or PaymentMethods.Card) return BillingErrors.BadPaymentMethod;
        if (method == null || (method == PaymentMethods.Balance && !session.UserId.HasValue))
            method = session.UserId.HasValue ? PaymentMethods.Balance : restMethod ?? PaymentMethods.Cash;
        var deskMethod = method == PaymentMethods.Balance ? restMethod ?? PaymentMethods.Cash : method;

        var now = _clock.UtcNow;
        var playedUntil = session.PausedUtc ?? now;
        decimal amountDue = 0, debt = 0;
        var charge = Charge.None;
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DeviceId);

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var segments = await _db.SessionSegments.Where(s => s.SessionId == id).ToListAsync();
            foreach (var open in segments.Where(s => s.EndUtc == null)) open.EndUtc = playedUntil;

            PriceQuote? quote = null;
            if (!session.IsPrepaid)
            {
                quote = await QuotePlayedAsync(session, segments, playedUntil);
                if (quote != null) session.TotalCost = quote.Total;
            }
            session.EndUtc = now;
            session.Status = "ended";
            session.PausedUtc = null;

            try
            {
                await _db.SaveChangesAsync(); // fails if someone else ended or changed the session
            }
            catch (DbUpdateConcurrencyException)
            {
                return BillingErrors.SessionChanged;
            }

            if (!session.IsPrepaid && session.TotalCost > 0)
            {
                var minutesText = $"{quote?.BilledMinutes ?? 0} хв";
                if (method == PaymentMethods.Balance)
                {
                    charge = await Wallet.ChargeUpToAsync(_db, session.UserId!.Value, session.TotalCost, SpendPurpose.PcTime, req?.UseBonus ?? true);
                    if (charge.Total > 0)
                    {
                        await Ledger.AppendAsync(_db, new LedgerEntryEntity
                        {
                            UserId = session.UserId, Type = LedgerTypes.SessionCharge, Method = PaymentMethods.Balance,
                            Amount = charge.Total, BalanceDelta = -charge.FromBalance, BonusDelta = -charge.FromBonus,
                            SessionId = session.Id, DeviceId = session.DeviceId,
                            Description = $"Сесія на {device?.Hostname ?? "?"} ({minutesText}) — {charge.Total} {cur}"
                        }, _actor, now);
                    }
                }
                amountDue = session.TotalCost - charge.Total;
                if (amountDue > 0 && deskMethod == PaymentMethods.Debt)
                {
                    // The player cannot pay: the rest goes on the player account, never lost
                    await Wallet.AddDebtAsync(_db, session.UserId!.Value, amountDue);
                    await Ledger.AppendAsync(_db, new LedgerEntryEntity
                    {
                        UserId = session.UserId, Type = LedgerTypes.SessionCharge, Method = PaymentMethods.Debt,
                        Amount = amountDue, DebtDelta = amountDue, SessionId = session.Id, DeviceId = session.DeviceId,
                        Description = $"Сесія на {device?.Hostname ?? "?"} ({minutesText}) — {amountDue} {cur} у борг"
                    }, _actor, now);
                    debt = amountDue;
                    amountDue = 0;
                }
                else if (amountDue > 0)
                {
                    // The rest is paid at the desk
                    await Ledger.AppendAsync(_db, new LedgerEntryEntity
                    {
                        UserId = session.UserId, Type = LedgerTypes.SessionCharge, Method = deskMethod,
                        Amount = amountDue, DeskAmount = amountDue, SessionId = session.Id, DeviceId = session.DeviceId,
                        Description = $"Сесія на {device?.Hostname ?? "?"} ({minutesText}) — {amountDue} {cur} на касі"
                    }, _actor, now);
                }
                var rest = amountDue + debt;
                session.PaymentMethod = charge.Total > 0 && rest > 0 ? $"{PaymentMethods.Balance}+{deskMethod}"
                    : charge.Total > 0 ? PaymentMethods.Balance : deskMethod;
                await _db.SaveChangesAsync();
            }

            await AuditAsync(AuditActions.SessionEnded, session.Id,
                $"Session on {device?.Hostname ?? "?"} ended: {session.TotalCost} {cur}" + (amountDue > 0 ? $", {amountDue} {cur} due at the desk" : "") +
                (debt > 0 ? $", {debt} {cur} recorded as debt" : "") + (why != null ? $" ({why})" : ""),
                new { session.DeviceId, cost = session.TotalCost, amountDue, debt, fromBalance = charge.FromBalance, fromBonus = charge.FromBonus,
                      prepaid = session.IsPrepaid, player = session.UserId, billedMinutes = quote?.BilledMinutes, reason = why });
            if (debt > 0 && _audit != null)
            {
                var owed = await Wallet.GetAccountsAsync(_db, session.UserId!.Value);
                await _audit.WriteAsync(AuditActions.PlayerDebtRecorded, "player", session.UserId,
                    $"Debt {debt} {cur} recorded: post-paid session on {device?.Hostname ?? "?"} not paid (debt now {owed?.Debt} {cur})",
                    new { amount = debt, sessionId = session.Id, device = device?.Hostname, cost = session.TotalCost, paidFromBalances = charge.Total, debtNow = owed?.Debt });
            }
            await tx.CommitAsync();
        }

        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = now }));
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.Lock, null));
        return BillingResult<EndResult>.Success(new EndResult(session, amountDue, charge, debt));
    }

    /// <summary>Current price of an open-ended session (also for the live display).</summary>
    public async Task<PriceQuote?> QuotePlayedAsync(SessionEntity session, IReadOnlyList<SessionSegmentEntity>? segments, DateTimeOffset until)
    {
        segments ??= await _db.SessionSegments.AsNoTracking().Where(s => s.SessionId == session.Id).ToListAsync();
        var spans = segments.Select(s => (s.TariffId ?? session.TariffId, s.StartUtc, End: s.EndUtc ?? until)).ToList();
        if (spans.Count == 0) spans.Add((session.TariffId, session.StartUtc, until)); // session of an older version
        var ids = spans.Where(s => s.Item1.HasValue).Select(s => s.Item1!.Value).Append(session.TariffId ?? Guid.Empty).Distinct().ToList();
        var pricing = await PricingAsync(ids);
        if (!session.TariffId.HasValue || !pricing.TryGetValue(session.TariffId.Value, out var billing))
        {
            billing = spans.Select(s => s.Item1).Where(t => t.HasValue && pricing.ContainsKey(t.Value)).Select(t => pricing[t!.Value]).LastOrDefault();
            if (billing == null) return null; // tariff deleted: nothing to bill
        }
        return TariffCalculator.QuotePlayed(
            spans.Select(s => (s.Item1.HasValue && pricing.TryGetValue(s.Item1.Value, out var p) ? p : billing, s.StartUtc, s.End)),
            billing, _clock.Zone);
    }

    // ===== Extend =====

    /// <summary>Adds minutes to a prepaid or package session, at the session tariff, never past a cut-off.</summary>
    public async Task<BillingResult<SessionEntity>> ExtendAsync(Guid id, ExtendSessionRequest req)
    {
        var cur = await _db.CurrencySymbolAsync();
        if (req.Minutes <= 0 || req.Minutes > 24 * 60) return BillingErrors.BadMinutes;
        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return BillingErrors.SessionNotFound;
        if (session.Status != "active") return BillingErrors.SessionNotActive;
        if (session.PausedUtc.HasValue) return BillingErrors.SessionPaused;
        if (!session.IsPrepaid || !session.EndUtc.HasValue) return BillingErrors.OpenSessionNoEnd;

        var now = _clock.UtcNow;
        var baseEnd = session.EndUtc.Value > now ? session.EndUtc.Value : now;
        var newEnd = baseEnd.AddMinutes(req.Minutes);
        if (session.CutoffUtc.HasValue && newEnd > session.CutoffUtc.Value)
            return BillingErrors.PastCutoff.With($"{_clock.ToLocal(session.CutoffUtc.Value):HH:mm}");

        var tariff = session.TariffId.HasValue ? await _db.Tariffs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == session.TariffId.Value) : null;
        var extraCost = tariff != null
            ? TariffCalculator.QuoteFixed(await PricingAsync(tariff), baseEnd, req.Minutes, _clock.Zone, applyMinimum: false).Total
            : 0m;
        var value = extraCost;
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DeviceId);
        var user = session.UserId.HasValue ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId.Value) : null;
        string? freeReason = null;
        BillingResult<string> resolved;
        if (IsFree(req.PaymentMethod))
        {
            var free = CheckFree(req.Reason);
            if (!free.Ok) return free.Error!;
            freeReason = free.Value;
            resolved = BillingResult<string>.Success(PaymentMethods.Free);
            extraCost = 0m;
        }
        else
        {
            resolved = ResolveMethod(req.PaymentMethod, user);
            if (!resolved.Ok) return resolved.Error!;
        }

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            if (extraCost > 0)
            {
                var paid = await PayAsync(user, resolved.Value!, extraCost, req.UseBonus, LedgerTypes.SessionCharge, session, now,
                    $"Продовження сесії на {device?.Hostname ?? "?"} (+{req.Minutes} хв) — {extraCost} {cur}");
                if (!paid.Ok) return paid.Error!;
            }

            session.EndUtc = newEnd;
            session.TotalCost += extraCost;
            session.PaidMinutes = (session.PaidMinutes ?? 0) + req.Minutes;
            try
            {
                await _db.SaveChangesAsync(); // fails if the session was ended or extended in between
            }
            catch (DbUpdateConcurrencyException)
            {
                return BillingErrors.SessionChanged;
            }
            await AuditAsync(AuditActions.SessionExtended, session.Id,
                $"Session on {device?.Hostname ?? "?"} extended by {req.Minutes} min, {extraCost} {cur} " + PaidText(resolved.Value),
                new { device?.Hostname, minutes = req.Minutes, cost = extraCost, player = user?.Username, paymentMethod = resolved.Value, newEndUtc = session.EndUtc });
            if (extraCost == 0)
            {
                await AuditAsync(AuditActions.SessionFreeTime, session.Id,
                    $"Free time on {device?.Hostname ?? "?"}: +{req.Minutes} min, worth {value} {cur}" +
                    (freeReason != null ? $": {freeReason}" : tariff == null ? " (no tariff)" : $" (price 0 {cur})"),
                    new { device?.Hostname, minutes = req.Minutes, value, reason = freeReason, player = user?.Username });
            }
            await tx.CommitAsync();
        }

        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = session.EndUtc }));
        return BillingResult<SessionEntity>.Success(session);
    }

    // ===== Pause / resume =====

    /// <summary>Stops the time: the PC locks; post-paid time is not billed while paused.</summary>
    public async Task<BillingResult<SessionEntity>> PauseAsync(Guid id)
    {
        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return BillingErrors.SessionNotFound;
        if (session.Status != "active") return BillingErrors.SessionNotActive;
        if (session.PausedUtc.HasValue) return BillingErrors.SessionPaused;
        var now = _clock.UtcNow;
        if (IsDue(session, now)) return BillingErrors.SessionExpired;

        session.PausedUtc = now;
        foreach (var open in await _db.SessionSegments.Where(s => s.SessionId == id && s.EndUtc == null).ToListAsync())
            open.EndUtc = now;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return BillingErrors.SessionChanged;
        }

        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DeviceId);
        var left = session.EndUtc.HasValue ? (int)Math.Round((session.EndUtc.Value - now).TotalMinutes) : (int?)null;
        await AuditAsync(AuditActions.SessionPaused, session.Id,
            $"Session on {device?.Hostname ?? "?"} paused" + (left.HasValue ? $" with {left} min left" : ""),
            new { device?.Hostname, minutesLeft = left, cutoffUtc = session.CutoffUtc });

        // Agent: no end time while paused (the countdown stops), then lock
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = (DateTimeOffset?)null }));
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.Lock, null));
        return BillingResult<SessionEntity>.Success(session);
    }

    /// <summary>Starts the time again; a prepaid end moves by the paused time, never past the cut-off.</summary>
    public async Task<BillingResult<SessionEntity>> ResumeAsync(Guid id)
    {
        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return BillingErrors.SessionNotFound;
        if (session.Status != "active") return BillingErrors.SessionNotActive;
        if (!session.PausedUtc.HasValue) return BillingErrors.SessionNotPaused;

        var now = _clock.UtcNow;
        var paused = now - session.PausedUtc.Value;
        if (paused < TimeSpan.Zero) paused = TimeSpan.Zero;
        if (session.EndUtc.HasValue && session.IsPrepaid)
        {
            var newEnd = session.EndUtc.Value + paused;
            if (session.CutoffUtc.HasValue && newEnd > session.CutoffUtc.Value) newEnd = session.CutoffUtc.Value;
            if (newEnd <= now) return BillingErrors.SessionExpired; // the cut-off passed while paused
            session.EndUtc = newEnd;
        }
        session.PausedUtc = null;
        _db.SessionSegments.Add(NewSegment(session, now));
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return BillingErrors.SessionChanged;
        }

        var agentEnd = session.EndUtc;
        if (session.LimitToBalance) agentEnd = await RefreshBalanceEndAsync(session, now, send: false) ?? agentEnd;
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DeviceId);
        await AuditAsync(AuditActions.SessionResumed, session.Id,
            $"Session on {device?.Hostname ?? "?"} resumed after {(int)Math.Round(paused.TotalMinutes)} min pause",
            new { device?.Hostname, pausedMinutes = Math.Round(paused.TotalMinutes, 1), newEndUtc = session.EndUtc });

        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = agentEnd }));
        _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.Unlock, null));
        return BillingResult<SessionEntity>.Success(session);
    }

    // ===== Transfer =====

    public async Task<BillingResult<TransferQuote>> QuoteTransferAsync(Guid id, Guid targetDeviceId)
    {
        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (session == null) return BillingErrors.SessionNotFound;
        var plan = await PlanTransferAsync(session, targetDeviceId, _clock.UtcNow);
        return plan.Ok ? BillingResult<TransferQuote>.Success(plan.Value!.Quote) : plan.Error!;
    }

    /// <summary>Moves an active (also paused) session to a free PC; see the class comment for the price rule.</summary>
    public async Task<BillingResult<(SessionEntity Session, TransferQuote Quote)>> TransferAsync(Guid id, TransferSessionRequest req)
    {
        var cur = await _db.CurrencySymbolAsync();
        var session = await _db.Sessions.FindAsync(id);
        if (session == null) return BillingErrors.SessionNotFound;
        var now = _clock.UtcNow;
        var planned = await PlanTransferAsync(session, req.TargetDeviceId, now);
        if (!planned.Ok) return planned.Error!;
        var plan = planned.Value!;
        var quote = plan.Quote;

        var user = session.UserId.HasValue ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId.Value) : null;
        string? method = null;
        if (quote.Surcharge > 0)
        {
            var resolved = ResolveMethod(req.PaymentMethod, user);
            if (!resolved.Ok) return resolved.Error!;
            method = resolved.Value;
        }
        var fromDeviceId = session.DeviceId;
        var paused = session.PausedUtc.HasValue;

        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            try
            {
                if (quote.Surcharge > 0)
                {
                    var paid = await PayAsync(user, method!, quote.Surcharge, req.UseBonus, LedgerTypes.SessionCharge, session, now,
                        $"Доплата за перехід {plan.FromHostname} → {quote.TargetHostname} — {quote.Surcharge} {cur}");
                    if (!paid.Ok) return paid.Error!;
                }

                foreach (var open in await _db.SessionSegments.Where(s => s.SessionId == id && s.EndUtc == null).ToListAsync())
                    open.EndUtc = now;
                session.DeviceId = req.TargetDeviceId;
                session.TariffId = quote.TargetTariffId ?? session.TariffId;
                session.PackageId = quote.TargetPackageId ?? session.PackageId;
                session.TotalCost += quote.Surcharge;
                if (!paused) _db.SessionSegments.Add(NewSegment(session, now));
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return BillingErrors.SessionChanged;
            }
            catch (DbUpdateException)
            {
                return BillingErrors.DeviceBusy; // the target PC got a session at the same moment
            }

            await AuditAsync(AuditActions.SessionTransferred, session.Id,
                $"Session moved from {plan.FromHostname} to {quote.TargetHostname}" +
                (quote.Surcharge > 0 ? $", price difference {quote.Surcharge} {cur} " + PaidText(method) : ", no price difference"),
                new { from = plan.FromHostname, to = quote.TargetHostname, fromDeviceId, toDeviceId = req.TargetDeviceId,
                      surcharge = quote.Surcharge, basis = quote.Basis, remainingMinutes = quote.RemainingMinutes,
                      tariff = quote.TargetTariffName, package = quote.TargetPackageName, paymentMethod = method, paused });
            await tx.CommitAsync();
        }

        // Old PC: lock; new PC: the session end, then unlock (a paused session stays locked)
        _devices.EnqueueCommand(fromDeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = now }));
        _devices.EnqueueCommand(fromDeviceId, new EnqueueCommandRequest(CommandTypes.Lock, null));
        if (!paused)
        {
            // From the balance: the new zone may have other prices
            var agentEnd = session.LimitToBalance ? await RefreshBalanceEndAsync(session, now, send: false) ?? session.BalanceEndUtc : session.EndUtc;
            _devices.EnqueueCommand(req.TargetDeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = agentEnd }));
            _devices.EnqueueCommand(req.TargetDeviceId, new EnqueueCommandRequest(CommandTypes.Unlock, null));
        }
        return BillingResult<(SessionEntity, TransferQuote)>.Success((session, quote));
    }

    private sealed record TransferPlan(TransferQuote Quote, string FromHostname);

    private async Task<BillingResult<TransferPlan>> PlanTransferAsync(SessionEntity session, Guid targetDeviceId, DateTimeOffset now)
    {
        if (session.Status != "active") return BillingErrors.SessionNotActive;
        if (IsDue(session, now)) return BillingErrors.SessionExpired;
        if (session.DeviceId == targetDeviceId) return BillingErrors.SameDevice;
        var target = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == targetDeviceId);
        if (target == null) return BillingErrors.DeviceNotFound;
        if (target.Status != DeviceStatuses.Approved) return BillingErrors.DeviceNotApproved;
        var busy = await _db.Sessions.AsNoTracking().Where(s => s.DeviceId == targetDeviceId && s.Status == "active").ToListAsync();
        if (busy.Any(s => !IsDue(s, now))) return BillingErrors.DeviceBusy;
        var source = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DeviceId);
        var sameZone = source?.ZoneId == target.ZoneId;

        // Remaining paid time (prepaid and packages)
        var remaining = TimeSpan.Zero;
        if (session.IsPrepaid && session.EndUtc.HasValue)
            remaining = session.EndUtc.Value - (session.PausedUtc ?? now);
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var remainingMinutes = (int)Math.Ceiling(remaining.TotalMinutes);

        var sourceTariff = session.TariffId.HasValue ? await _db.Tariffs.AsNoTracking().FirstOrDefaultAsync(t => t.Id == session.TariffId.Value) : null;
        var targetTariff = sameZone && sourceTariff != null ? sourceTariff : await ZoneTariffAsync(_db, target.ZoneId);
        if (targetTariff == null && !session.IsPrepaid) return BillingErrors.NoTariff;

        decimal surcharge = 0;
        string basis = "none";
        PackageEntity? targetPackage = null;
        if (!session.IsPrepaid)
        {
            basis = "segments"; // billed at the end, each segment at its own tariff
        }
        else if (!sameZone || sourceTariff?.Id != targetTariff?.Id)
        {
            var sourcePackage = session.PackageId.HasValue ? await _db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == session.PackageId.Value) : null;
            if (sourcePackage != null && !sameZone)
            {
                var zoneName = await _db.Zones.AsNoTracking().Where(z => z.Id == target.ZoneId).Select(z => z.Name).FirstOrDefaultAsync() ?? "—";
                targetPackage = (await _db.Packages.AsNoTracking().Where(p => p.ZoneId == target.ZoneId && p.IsActive).ToListAsync())
                    .Where(p => PackageWindows.SameKind(p, sourcePackage)).OrderBy(p => p.SortOrder).FirstOrDefault();
                if (targetPackage == null) return BillingErrors.PackageNoMatchInZone.With(zoneName);
                var totalMinutes = session.PaidMinutes is > 0 ? session.PaidMinutes.Value
                    : session.EndUtc.HasValue ? (session.EndUtc.Value - session.StartUtc).TotalMinutes : 0;
                var share = totalMinutes > 0 ? Math.Min(1m, (decimal)(remaining.TotalMinutes / totalMinutes)) : 0m;
                surcharge = TariffCalculator.Round(Math.Max(0m, (targetPackage.Price - sourcePackage.Price) * share));
                basis = "package";
            }
            else if (sourceTariff != null && targetTariff != null && remaining > TimeSpan.Zero)
            {
                var end = now + remaining;
                var oldPrice = TariffCalculator.Slice(await PricingAsync(sourceTariff), now, end, _clock.Zone).Sum(s => s.Amount);
                var newPrice = TariffCalculator.Slice(await PricingAsync(targetTariff), now, end, _clock.Zone).Sum(s => s.Amount);
                surcharge = TariffCalculator.Round(Math.Max(0m, newPrice - oldPrice));
                basis = "tariff";
            }
        }

        return BillingResult<TransferPlan>.Success(new TransferPlan(new TransferQuote(
            session.Id, target.Id, target.Hostname, target.ZoneId,
            targetTariff?.Id, targetTariff?.Name, targetPackage?.Id, targetPackage?.Name,
            surcharge, remainingMinutes, basis), source?.Hostname ?? "?"));
    }

    // ===== Expiry =====

    /// <summary>
    /// A prepaid or package session is due to close when its time ran out (not while paused), or
    /// when a paused session reached its cut-off.
    /// </summary>
    public static bool IsDue(SessionEntity s, DateTimeOffset now) =>
        s.Status == "active" && s.IsPrepaid && s.EndUtc.HasValue &&
        (s.PausedUtc == null ? s.EndUtc.Value <= now : s.CutoffUtc.HasValue && s.CutoffUtc.Value <= now);

    /// <summary>Marks due sessions as ended and closes their segments (caller saves). Returns the segments.</summary>
    private async Task<List<SessionSegmentEntity>> CloseAsync(IReadOnlyCollection<SessionEntity> sessions, DateTimeOffset now)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var open = await _db.SessionSegments.Where(s => ids.Contains(s.SessionId) && s.EndUtc == null).ToListAsync();
        foreach (var session in sessions)
        {
            if (session.PausedUtc.HasValue) session.EndUtc = session.CutoffUtc;
            session.PausedUtc = null;
            session.Status = "ended";
            var end = session.EndUtc.HasValue && session.EndUtc.Value < now ? session.EndUtc.Value : now;
            foreach (var segment in open.Where(s => s.SessionId == session.Id)) segment.EndUtc = end;
        }
        return open;
    }

    /// <summary>
    /// Closes due prepaid sessions one by one (a session that staff change at this moment is skipped
    /// and checked again next time) and locks their PCs. Returns the number closed.
    /// </summary>
    public async Task<int> CloseExpiredAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var candidates = await _db.Sessions.Where(s => s.Status == "active" && s.IsPrepaid && s.EndUtc != null).ToListAsync(ct);
        var closed = 0;
        foreach (var session in candidates.Where(s => IsDue(s, now)))
        {
            var segments = await CloseAsync(new[] { session }, now);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Changed by staff at the same moment (for example extended); checked again later
                _db.Entry(session).State = EntityState.Detached;
                foreach (var segment in segments) _db.Entry(segment).State = EntityState.Detached;
                continue;
            }
            closed++;
            _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = now }));
            _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.Lock, null));
        }
        return closed + await CheckBalanceLimitsAsync(now, ct);
    }

    // ===== Sessions from the balance (player login) =====

    /// <summary>A session from the balance must buy at least this many minutes at the start.</summary>
    public const int MinBalanceMinutes = 1;
    /// <summary>The projection looks at most this far ahead (it is re-computed every 5 s anyway).</summary>
    public const int MaxBalanceMinutes = 24 * 60;
    /// <summary>A new end time goes to the agent when it moved by this much (a top-up, a bar purchase from the balance).</summary>
    public static readonly TimeSpan BalanceEndResend = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The latest moment, in whole minutes from now (at most 24 h), at which the played time costs no
    /// more than the available money. The cost is the exact post-paid price (rules, step rounding,
    /// minimum charge), which never decreases with time, so a binary search finds it.
    /// </summary>
    public static DateTimeOffset ProjectEnd(Func<DateTimeOffset, decimal> cost, decimal available, DateTimeOffset now)
    {
        if (cost(now) > available) return now;
        if (cost(now.AddMinutes(MaxBalanceMinutes)) <= available) return now.AddMinutes(MaxBalanceMinutes);
        int lo = 0, hi = MaxBalanceMinutes;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (cost(now.AddMinutes(mid)) <= available) lo = mid;
            else hi = mid - 1;
        }
        return now.AddMinutes(lo);
    }

    /// <summary>When the balances of the player run out for this session (null: no tariff or no player).</summary>
    public async Task<DateTimeOffset?> ProjectBalanceEndAsync(SessionEntity session, DateTimeOffset now)
    {
        if (!session.UserId.HasValue) return null;
        var accounts = await Wallet.GetAccountsAsync(_db, session.UserId.Value);
        if (accounts == null) return null;
        var segments = await _db.SessionSegments.AsNoTracking().Where(s => s.SessionId == session.Id).ToListAsync();
        var ids = segments.Where(s => s.TariffId.HasValue).Select(s => s.TariffId!.Value).Append(session.TariffId ?? Guid.Empty).Distinct().ToList();
        var pricing = await PricingAsync(ids);
        if (!session.TariffId.HasValue || !pricing.TryGetValue(session.TariffId.Value, out var billing)) return null;
        decimal Cost(DateTimeOffset until) => TariffCalculator.QuotePlayed(
            segments.Select(s => (s.TariffId.HasValue && pricing.TryGetValue(s.TariffId.Value, out var p) ? p : billing, s.StartUtc, s.EndUtc ?? until)),
            billing, _clock.Zone).Total;
        return ProjectEnd(Cost, accounts.Balance + accounts.Bonus, now);
    }

    /// <summary>Stores a new balance end (and sends it to the agent when send is true and it moved). Returns it.</summary>
    private async Task<DateTimeOffset?> RefreshBalanceEndAsync(SessionEntity session, DateTimeOffset now, bool send)
    {
        var end = await ProjectBalanceEndAsync(session, now);
        if (end == null) return null;
        var moved = session.BalanceEndUtc is not { } before || (before - end.Value).Duration() >= BalanceEndResend;
        if (!moved) return session.BalanceEndUtc;
        session.BalanceEndUtc = end;
        await _db.SaveChangesAsync();
        if (send) _devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest(CommandTypes.SessionSet, new { endUtc = end }));
        return end;
    }

    /// <summary>
    /// Every 5 s: a running session from the balance ends when its played time reaches the balances
    /// (billed from them; a rest, after a bar purchase from the balance, becomes debt), else its end is
    /// moved when a top-up or a purchase changed it. Returns the number ended.
    /// </summary>
    private async Task<int> CheckBalanceLimitsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var limited = await _db.Sessions.Where(s => s.Status == "active" && s.LimitToBalance && s.PausedUtc == null).ToListAsync(ct);
        var ended = 0;
        foreach (var session in limited)
        {
            try
            {
                var end = await ProjectBalanceEndAsync(session, now);
                if (end == null) continue;
                if (end <= now)
                {
                    var result = await EndAsync(session.Id, new EndSessionRequest(PaymentMethods.Balance, true, PaymentMethods.Debt), "balance used up");
                    if (result.Ok) ended++;
                    continue;
                }
                await RefreshBalanceEndAsync(session, now, send: true);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.Entry(session).State = EntityState.Detached; // changed by staff at the same moment; next time
            }
        }
        return ended;
    }

    // ===== Helpers =====

    private static SessionSegmentEntity NewSegment(SessionEntity session, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), SessionId = session.Id, DeviceId = session.DeviceId, TariffId = session.TariffId, StartUtc = now
    };

    private static BillingResult<string> ResolveMethod(string? requested, UserEntity? user)
    {
        var method = PaymentMethods.Normalize(requested);
        if (method == string.Empty) return BillingErrors.BadPaymentMethod;
        method ??= user != null ? PaymentMethods.Balance : PaymentMethods.Cash;
        if (method == PaymentMethods.Balance && user == null) return BillingErrors.GuestNeedsDesk;
        return BillingResult<string>.Success(method);
    }

    private static string PaidText(string? method) => method switch
    {
        PaymentMethods.Free => "free (no payment)",
        PaymentMethods.Balance => "from the balance",
        PaymentMethods.Card => "paid by card at the desk",
        PaymentMethods.Cash => "paid in cash at the desk",
        _ => ""
    };

    /// <summary>Takes the price from the balances (bonus first) or records a desk payment; one ledger row.</summary>
    private async Task<BillingResult<Charge>> PayAsync(UserEntity? user, string method, decimal amount, bool useBonus, string type,
        SessionEntity session, DateTimeOffset now, string description)
    {
        var entry = new LedgerEntryEntity
        {
            UserId = user?.Id, Type = type, Method = method, Amount = amount,
            SessionId = session.Id, PackageId = session.PackageId, DeviceId = session.DeviceId, Description = description
        };
        var charge = Charge.None;
        if (method == PaymentMethods.Balance)
        {
            var taken = await Wallet.TryChargeAsync(_db, user!.Id, amount, SpendPurpose.PcTime, useBonus);
            if (taken == null)
            {
                var have = await Wallet.GetAsync(_db, user.Id);
                return BillingErrors.InsufficientBalance.With(amount, have?.Balance ?? 0m, have?.Bonus ?? 0m);
            }
            charge = taken;
            entry.BalanceDelta = -charge.FromBalance;
            entry.BonusDelta = -charge.FromBonus;
        }
        else
        {
            entry.DeskAmount = amount;
        }
        await Ledger.AppendAsync(_db, entry, _actor, now);
        return BillingResult<Charge>.Success(charge);
    }

    /// <summary>Tariff of a zone (its default one first), else the club default tariff.</summary>
    public static async Task<TariffEntity?> ZoneTariffAsync(CmsDbContext db, Guid? zoneId)
    {
        if (zoneId.HasValue)
        {
            var inZone = (await db.Tariffs.AsNoTracking().Where(t => t.ZoneId == zoneId).ToListAsync())
                .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name).FirstOrDefault();
            if (inZone != null) return inZone;
        }
        return (await db.Tariffs.AsNoTracking().Where(t => t.IsDefault).ToListAsync())
            .OrderBy(t => t.ZoneId.HasValue).FirstOrDefault();
    }

    private async Task<TariffPricing> PricingAsync(TariffEntity tariff)
    {
        var rules = await _db.TariffRules.AsNoTracking().Where(r => r.TariffId == tariff.Id).ToListAsync();
        return TariffPricing.From(tariff, rules);
    }

    private async Task<Dictionary<Guid, TariffPricing>> PricingAsync(IReadOnlyCollection<Guid> tariffIds)
    {
        var tariffs = await _db.Tariffs.AsNoTracking().Where(t => tariffIds.Contains(t.Id)).ToListAsync();
        var rules = await _db.TariffRules.AsNoTracking().Where(r => tariffIds.Contains(r.TariffId)).ToListAsync();
        return tariffs.ToDictionary(t => t.Id, t => TariffPricing.From(t, rules));
    }
}
