using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Pos;

/// <summary>
/// Club settings of the bar orders from the lock screens (Owner, Manager; ClubSettings key bar.orders, JSON):
///   Enabled (true)    - the lock screens show the Order button during a session
///   MaxAmount (500)   - the most one order may cost
/// </summary>
public sealed record BarOrderSettings
{
    public bool Enabled { get; init; } = true;
    public decimal MaxAmount { get; init; } = 500m;

    public static readonly BarOrderSettings Default = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? Validate() => MaxAmount is < 10m or > 10_000m ? "The most an order may cost: 10 to 10000." : null;

    public static async Task<BarOrderSettings> LoadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var json = await db.ClubSettings.AsNoTracking().Where(s => s.Key == ClubSettingKeys.BarOrders).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var settings = JsonSerializer.Deserialize<BarOrderSettings>(json, Json) ?? Default;
            return settings.Validate() == null ? settings : Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static async Task<BarOrderSettings> SaveAsync(CmsDbContext db, BarOrderSettings settings, string? by, DateTimeOffset now, CancellationToken ct = default)
    {
        var before = await LoadAsync(db, ct);
        var row = await db.ClubSettings.FirstOrDefaultAsync(s => s.Key == ClubSettingKeys.BarOrders, ct);
        if (row == null)
        {
            row = new ClubSettingEntity { Key = ClubSettingKeys.BarOrders };
            db.ClubSettings.Add(row);
        }
        row.Value = JsonSerializer.Serialize(settings, Json);
        row.UpdatedUtc = now;
        row.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        return before;
    }
}

public static class BarOrderErrors
{
    private const int Conflict = StatusCodes.Status409Conflict;
    public static readonly BillingError NotFound = new("order_not_found", "Order not found", StatusCodes.Status404NotFound);
    public static readonly BillingError Disabled = new("orders_disabled", "Bar orders from the PCs are off.", Conflict);
    public static readonly BillingError NoSession = new("order_needs_session", "Orders need a running session on this PC.", Conflict);
    public static readonly BillingError OneOpen = new("order_already_open", "This PC already has an open order.", Conflict);
    public static readonly BillingError BadLines = new("order_bad_lines", "Choose 1 to 10 products, 1 to 10 of each.");
    public static readonly BillingError TooMuch = new("order_too_much", "An order may cost at most {0}.");
    public static readonly BillingError BalanceNeedsPlayer = new("order_balance_needs_player", "Only a player's session can pay from the balance; pay at the desk.");
    public static readonly BillingError BadPayment = new("order_bad_payment", "Payment must be balance or desk.");
    public static readonly BillingError NotOpen = new("order_not_open", "The order is already delivered or cancelled.", Conflict);
    public static readonly BillingError PlayerCancelTooLate = new("order_preparing", "The bar is already preparing the order; ask the staff.", Conflict);
    public static readonly BillingError DeskMethodRequired = new("order_desk_method", "Choose how the guest pays at the desk: cash or card.");
    public static readonly BillingError BadSettings = new("bad_order_settings", "{0}");
}

public sealed record OrderLine(Guid ProductId, int Quantity);

/// <summary>
/// Bar orders from the lock screen (spec areas 1 and 5 v1). The player orders during a session (one open order per
/// PC, at most MaxAmount): paid from the REAL balance at once (bonus never pays at the bar), held in the ledger
/// (order_hold) until the delivery, or paid at the desk on delivery. Staff move it New -> Preparing -> Delivered, or
/// cancel it. The bar sale (receipt, stock, ledger, Z-report, analytics) is recorded at the delivery with its payment
/// method, at the prices of the order: a prepaid order releases its hold (order_release) and the sale takes the same
/// money from the balance in the same transaction. A cancel returns a prepaid order to the balance. Every change that
/// moves money is audited.
/// </summary>
public sealed class BarOrderService
{
    public const int MaxLines = 10;
    public const int MaxQuantity = 10;

    private readonly CmsDbContext _db;
    private readonly ClubClock _clock;
    private readonly AuditLogger? _audit;
    private readonly StaffActor? _actor;
    private readonly StaffUserEntity? _auditActor;

    public BarOrderService(CmsDbContext db, ClubClock? clock = null, AuditLogger? audit = null, StaffActor? actor = null, StaffUserEntity? auditActor = null)
    {
        _db = db;
        _clock = clock ?? ClubClock.System();
        _audit = audit;
        _actor = actor ?? (auditActor == null ? null : new StaffActor(auditActor.Id, auditActor.Username));
        _auditActor = auditActor;
    }

    private Task AuditAsync(string action, long id, string summary, object details) =>
        _audit?.WriteAsync(action, "order", id, summary, details, _auditActor) ?? Task.CompletedTask;

    /// <summary>The active, not paused session of the PC (null: no order possible).</summary>
    private Task<SessionEntity?> SessionAsync(Guid deviceId) =>
        _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.DeviceId == deviceId && s.Status == "active" && s.PausedUtc == null);

    /// <summary>What the lock screen offers: active products in stock (tracked ones: at most the stock), the limits, the balance.</summary>
    public async Task<BarMenuView> MenuAsync(Guid deviceId)
    {
        var settings = await BarOrderSettings.LoadAsync(_db);
        var currency = await _db.CurrencyAsync();
        var session = await SessionAsync(deviceId);
        var products = (await _db.Products.AsNoTracking().Where(p => p.IsActive && (!p.TrackStock || p.StockQuantity > 0)).ToListAsync())
            .OrderBy(p => p.Category).ThenBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new BarMenuItem(p.Id, p.Name, p.Category, p.Price, p.TrackStock ? Math.Min(p.StockQuantity, MaxQuantity) : MaxQuantity)).ToList();
        var balance = session?.UserId is { } uid ? (await Wallet.GetAsync(_db, uid))?.Balance : null;
        var open = await _db.BarOrders.AsNoTracking().AnyAsync(o => o.DeviceId == deviceId && (o.Status == BarOrderStatuses.New || o.Status == BarOrderStatuses.Preparing));
        return new BarMenuView(settings.Enabled && session != null, currency.Code, currency.Symbol, settings.MaxAmount, MaxLines, MaxQuantity,
            session?.UserId != null, balance, open, products);
    }

    public async Task<BillingResult<BarOrderEntity>> PlaceAsync(Guid deviceId, IReadOnlyList<OrderLine>? lines, string? payment)
    {
        var cur = await _db.CurrencySymbolAsync();
        var settings = await BarOrderSettings.LoadAsync(_db);
        if (!settings.Enabled) return BarOrderErrors.Disabled;
        var session = await SessionAsync(deviceId);
        if (session == null) return BarOrderErrors.NoSession;
        if (lines == null || lines.Count == 0 || lines.Count > MaxLines || lines.Any(l => l.Quantity is < 1 or > MaxQuantity)) return BarOrderErrors.BadLines;
        var merged = lines.GroupBy(l => l.ProductId).Select(g => new OrderLine(g.Key, g.Sum(l => l.Quantity))).ToList();
        if (merged.Any(l => l.Quantity > MaxQuantity)) return BarOrderErrors.BadLines;
        var method = (payment ?? BarOrderPayments.Desk).Trim().ToLowerInvariant();
        if (method is not (BarOrderPayments.Balance or BarOrderPayments.Desk)) return BarOrderErrors.BadPayment;
        if (method == BarOrderPayments.Balance && session.UserId == null) return BarOrderErrors.BalanceNeedsPlayer;

        var ids = merged.Select(l => l.ProductId).ToList();
        var products = await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        foreach (var l in merged)
        {
            if (!products.TryGetValue(l.ProductId, out var p)) return PosErrors.ProductNotFound;
            if (!p.IsActive) return PosErrors.ProductInactive.With(p.Name);
            if (p.TrackStock && p.StockQuantity < l.Quantity) return PosErrors.OutOfStock.With(p.Name, p.StockQuantity);
        }
        var items = merged.Select(l => new BarOrderItemEntity
        {
            ProductId = l.ProductId, Name = products[l.ProductId].Name, UnitPrice = products[l.ProductId].Price, Quantity = l.Quantity,
            LineTotal = products[l.ProductId].Price * l.Quantity
        }).ToList();
        var total = items.Sum(i => i.LineTotal);
        if (total > settings.MaxAmount) return BarOrderErrors.TooMuch.With($"{settings.MaxAmount:0.00} {cur}");

        if (await _db.BarOrders.AnyAsync(o => o.DeviceId == deviceId && (o.Status == BarOrderStatuses.New || o.Status == BarOrderStatuses.Preparing)))
            return BarOrderErrors.OneOpen;
        var now = _clock.UtcNow;
        var host = await _db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";
        var order = new BarOrderEntity
        {
            DeviceId = deviceId, SessionId = session.Id, UserId = session.UserId, Status = BarOrderStatuses.New, Payment = method, Total = total,
            CreatedUtc = now, UpdatedUtc = now
        };
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (method == BarOrderPayments.Balance && await Wallet.TryChargeAsync(_db, session.UserId!.Value, total, SpendPurpose.Bar) == null)
        {
            var have = await Wallet.GetAsync(_db, session.UserId.Value);
            return PosErrors.InsufficientBalance.With(total, have?.Balance ?? 0m);
        }
        _db.BarOrders.Add(order);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _db.Entry(order).State = EntityState.Detached;
            return BarOrderErrors.OneOpen; // the unique index: an order of this PC was placed at the same moment
        }
        foreach (var i in items) i.OrderId = order.Id;
        _db.BarOrderItems.AddRange(items);
        await _db.SaveChangesAsync();
        if (method == BarOrderPayments.Balance)
        {
            await Ledger.AppendAsync(_db, new LedgerEntryEntity
            {
                UserId = session.UserId, Type = LedgerTypes.OrderHold, Method = PaymentMethods.Balance, Amount = total, BalanceDelta = -total,
                BarOrderId = order.Id, DeviceId = deviceId, Description = $"Замовлення бару №{order.Id} з {host}: {Items(items)} — {total} {cur} (утримано до видачі)"
            }, _actor, now);
        }
        var player = session.UserId.HasValue ? await _db.Users.AsNoTracking().Where(u => u.Id == session.UserId).Select(u => u.Username).FirstOrDefaultAsync() : null;
        await AuditAsync(AuditActions.OrderPlaced, order.Id,
            $"Bar order #{order.Id} from {host}: {Items(items)}, {total} {cur} " + (method == BarOrderPayments.Balance ? "paid from the balance (held)" : "to pay at the desk"),
            new { id = order.Id, device = host, player, total, payment = method, items = items.Select(i => $"{i.Name} x{i.Quantity}") });
        await tx.CommitAsync();
        return BillingResult<BarOrderEntity>.Success(order);
    }

    public static string Items(IEnumerable<BarOrderItemEntity> items) => string.Join(", ", items.Select(i => i.Quantity > 1 ? $"{i.Name} ×{i.Quantity}" : i.Name));

    public async Task<BillingResult<BarOrderEntity>> PrepareAsync(long id)
    {
        var order = await _db.BarOrders.FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return BarOrderErrors.NotFound;
        if (order.Status != BarOrderStatuses.New) return BarOrderErrors.NotOpen;
        order.Status = BarOrderStatuses.Preparing;
        order.UpdatedUtc = _clock.UtcNow;
        order.HandledByUsername = _actor?.Username;
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return BarOrderErrors.NotOpen; }
        return BillingResult<BarOrderEntity>.Success(order);
    }

    /// <summary>
    /// Delivered: the bar sale is recorded now (receipt, stock, ledger) at the prices of the order. Prepaid: the hold
    /// goes back on the balance and the sale takes it (balance); desk: cash or card (an open shift is needed).
    /// </summary>
    public async Task<BillingResult<BarOrderEntity>> DeliverAsync(long id, string? deskMethod)
    {
        var cur = await _db.CurrencySymbolAsync();
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        var order = await _db.BarOrders.FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return BarOrderErrors.NotFound;
        if (order.Status is not (BarOrderStatuses.New or BarOrderStatuses.Preparing)) return BarOrderErrors.NotOpen;
        string method;
        if (order.Payment == BarOrderPayments.Balance) method = PaymentMethods.Balance;
        else
        {
            method = PaymentMethods.Normalize(deskMethod) ?? string.Empty;
            if (method is not (PaymentMethods.Cash or PaymentMethods.Card)) return BarOrderErrors.DeskMethodRequired;
        }
        var items = await _db.BarOrderItems.AsNoTracking().Where(i => i.OrderId == id).OrderBy(i => i.Id).ToListAsync();
        // Claim the order first (concurrency stamp): two cashiers cannot deliver it twice
        order.Status = BarOrderStatuses.Delivered;
        order.DeliveredUtc = now;
        order.UpdatedUtc = now;
        order.HandledByUsername = _actor?.Username;
        order.DeskMethod = order.Payment == BarOrderPayments.Desk ? method : null;
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return BarOrderErrors.NotOpen; }
        if (order.Payment == BarOrderPayments.Balance)
            await ReleaseAsync(order, $"Замовлення бару №{order.Id} видано — {order.Total} {cur} знято з утримання для чека", now);
        var sale = await new SaleService(_db, _clock, _audit, _actor).SellAsync(
            items.Select(i => new SaleLine(i.ProductId, i.Quantity)).ToList(), method, order.UserId,
            items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.First().UnitPrice));
        if (!sale.Ok) return sale.Error!; // rolled back: the order stays open, the money stays held
        order.SaleId = sale.Value!.Id;
        await _db.SaveChangesAsync();
        var host = await _db.Devices.AsNoTracking().Where(d => d.Id == order.DeviceId).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";
        await AuditAsync(AuditActions.OrderDelivered, order.Id,
            $"Bar order #{order.Id} delivered to {host}: receipt #{sale.Value.Id}, {sale.Value.Total} {cur} ({method})",
            new { id = order.Id, device = host, sale = sale.Value.Id, total = sale.Value.Total, method });
        await tx.CommitAsync();
        return BillingResult<BarOrderEntity>.Success(order);
    }

    private async Task ReleaseAsync(BarOrderEntity order, string description, DateTimeOffset now)
    {
        await Wallet.CreditAsync(_db, order.UserId!.Value, order.Total);
        await Ledger.AppendAsync(_db, new LedgerEntryEntity
        {
            UserId = order.UserId, Type = LedgerTypes.OrderRelease, Method = PaymentMethods.Balance, Amount = order.Total, BalanceDelta = order.Total,
            BarOrderId = order.Id, DeviceId = order.DeviceId, Description = description
        }, _actor, now);
    }

    /// <summary>Cancel (staff any time before the delivery, the player only while it is new): a prepaid order goes back to the balance.</summary>
    public async Task<BillingResult<BarOrderEntity>> CancelAsync(long id, string? reason, bool byPlayer = false)
    {
        var cur = await _db.CurrencySymbolAsync();
        var now = _clock.UtcNow;
        var why = reason?.Trim();
        if (why is { Length: > 200 }) why = why[..200];
        if (string.IsNullOrEmpty(why)) why = byPlayer ? "cancelled by the player on the lock screen" : null;
        await using var tx = await _db.Database.BeginTransactionAsync();
        var order = await _db.BarOrders.FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return BarOrderErrors.NotFound;
        if (order.Status is not (BarOrderStatuses.New or BarOrderStatuses.Preparing)) return BarOrderErrors.NotOpen;
        if (byPlayer && order.Status != BarOrderStatuses.New) return BarOrderErrors.PlayerCancelTooLate;
        order.Status = BarOrderStatuses.Cancelled;
        order.CancelReason = why;
        order.UpdatedUtc = now;
        order.HandledByUsername = byPlayer ? null : _actor?.Username;
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return BarOrderErrors.NotOpen; }
        var refunded = 0m;
        if (order.Payment == BarOrderPayments.Balance)
        {
            await ReleaseAsync(order, $"Замовлення бару №{order.Id} скасовано — {order.Total} {cur} повернуто на баланс", now);
            refunded = order.Total;
        }
        var host = await _db.Devices.AsNoTracking().Where(d => d.Id == order.DeviceId).Select(d => d.Hostname).FirstOrDefaultAsync() ?? "?";
        await AuditAsync(AuditActions.OrderCancelled, order.Id,
            $"Bar order #{order.Id} of {host} cancelled" + (byPlayer ? " by the player" : "") + (refunded > 0 ? $", {refunded} {cur} back to the balance" : "") + (why != null ? $": {why}" : ""),
            new { id = order.Id, device = host, refunded, reason = why, byPlayer });
        await tx.CommitAsync();
        return BillingResult<BarOrderEntity>.Success(order);
    }

    /// <summary>The open order of the PC (the player cancels it on the lock screen).</summary>
    public Task<BarOrderEntity?> OpenOrderAsync(Guid deviceId) =>
        _db.BarOrders.AsNoTracking().FirstOrDefaultAsync(o => o.DeviceId == deviceId && (o.Status == BarOrderStatuses.New || o.Status == BarOrderStatuses.Preparing));
}

public record BarMenuItem(Guid Id, string Name, string Category, decimal Price, int MaxQuantity);
/// <summary>Available: orders on and a running session. CanPayFromBalance: a player's session; Balance: the real balance.</summary>
public record BarMenuView(bool Available, string Currency, string CurrencySymbol, decimal MaxAmount, int MaxLines, int MaxQuantity,
    bool CanPayFromBalance, decimal? Balance, bool HasOpenOrder, IReadOnlyList<BarMenuItem> Products);
