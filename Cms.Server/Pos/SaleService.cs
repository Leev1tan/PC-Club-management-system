using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Pos;

public sealed record SaleLine(Guid ProductId, int Quantity);

/// <summary>
/// Bar / POS (spec area 5): one receipt per sale, paid in cash, by card (both need an open shift)
/// or from the player's REAL balance (bonus pays only for PC time). Tracked products lose stock in
/// the same transaction and a sale never takes more than the stock. A refund (Owner, Manager, with a
/// reason) is its own receipt, paid back the same way (cash and card from the open shift, balance
/// back to the balance); the goods go back to the stock unless told otherwise. One refund per sale.
/// Lock order (PostgreSQL): player row, product rows (by id), open shift row, audit chain.
/// </summary>
public sealed class SaleService
{
    public const int MaxLines = 50;
    public const int MaxQuantity = 100;
    public const decimal MaxTotal = 100_000m;

    private readonly CmsDbContext _db;
    private readonly ClubClock _clock;
    private readonly AuditLogger? _audit;
    private readonly StaffActor? _actor;

    public SaleService(CmsDbContext db, ClubClock? clock = null, AuditLogger? audit = null, StaffActor? actor = null)
    {
        _db = db;
        _clock = clock ?? ClubClock.System();
        _audit = audit;
        _actor = actor;
    }

    /// <param name="fixedPrices">Unit prices to sell at (a bar order sells at the prices of the moment it was ordered).</param>
    public async Task<BillingResult<SaleEntity>> SellAsync(IReadOnlyList<SaleLine>? lines, string? paymentMethod, Guid? userId,
        IReadOnlyDictionary<Guid, decimal>? fixedPrices = null)
    {
        var cur = await _db.CurrencySymbolAsync();
        if (lines == null || lines.Count == 0) return PosErrors.EmptySale;
        if (lines.Count > MaxLines || lines.Any(l => l.Quantity is < 1 or > MaxQuantity)) return PosErrors.BadQuantity;
        var merged = lines.GroupBy(l => l.ProductId).Select(g => new SaleLine(g.Key, g.Sum(l => l.Quantity))).ToList();
        if (merged.Any(l => l.Quantity > MaxQuantity)) return PosErrors.BadQuantity;

        var method = PaymentMethods.Normalize(paymentMethod) ?? (userId.HasValue ? PaymentMethods.Balance : PaymentMethods.Cash);
        if (method == string.Empty) return BillingErrors.BadPaymentMethod;
        if (method == PaymentMethods.Balance && !userId.HasValue) return BillingErrors.GuestNeedsDesk;
        if (userId.HasValue && !await _db.Users.AnyAsync(u => u.Id == userId.Value)) return BillingErrors.UserNotFound;

        var ids = merged.Select(l => l.ProductId).ToList();
        var products = await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        foreach (var line in merged)
        {
            if (!products.TryGetValue(line.ProductId, out var p)) return PosErrors.ProductNotFound;
            if (!p.IsActive) return PosErrors.ProductInactive.With(p.Name);
        }
        var items = merged.Select(l =>
        {
            var p = products[l.ProductId];
            var price = fixedPrices != null && fixedPrices.TryGetValue(p.Id, out var fixedPrice) ? fixedPrice : p.Price;
            return new SaleItemEntity { ProductId = p.Id, Name = p.Name, Category = p.Category, UnitPrice = price, Quantity = l.Quantity, LineTotal = price * l.Quantity };
        }).ToList();
        var total = items.Sum(i => i.LineTotal);
        if (total <= 0 || total > MaxTotal) return BillingErrors.BadAmount;
        if (PaymentMethods.IsDesk(method) && _db.RequireOpenShift && await ShiftService.OpenShiftAsync(_db) == null) return ShiftErrors.Required;

        var now = _clock.UtcNow;
        // The delivery of a bar order runs this inside its own transaction (it commits both)
        await using var tx = await JoinedTransaction.BeginAsync(_db);
        if (userId.HasValue)
        {
            if (method == PaymentMethods.Balance)
            {
                if (await Wallet.TryChargeAsync(_db, userId.Value, total, SpendPurpose.Bar) == null)
                {
                    var have = await Wallet.GetAsync(_db, userId.Value);
                    return PosErrors.InsufficientBalance.With(total, have?.Balance ?? 0m);
                }
            }
            else
            {
                await LockPlayerAsync(userId.Value); // the ledger row locks it too; take it first (lock order)
            }
        }

        foreach (var item in items.Where(i => products[i.ProductId].TrackStock).OrderBy(i => i.ProductId))
        {
            if (!await Stock.TryTakeAsync(_db, item.ProductId, item.Quantity))
                return PosErrors.OutOfStock.With(item.Name, await Stock.QuantityAsync(_db, item.ProductId));
        }

        var shiftId = await ShiftService.LockOpenShiftIdAsync(_db);
        var sale = new SaleEntity
        {
            CreatedUtc = now, Kind = SaleKinds.Sale, ShiftId = shiftId, UserId = userId, Method = method, Total = total,
            ActorId = _actor?.Id, ActorUsername = _actor?.Username
        };
        _db.Sales.Add(sale);
        await _db.SaveChangesAsync();
        foreach (var item in items) item.SaleId = sale.Id;
        _db.SaleItems.AddRange(items);
        await _db.SaveChangesAsync();
        foreach (var item in items.Where(i => products[i.ProductId].TrackStock).OrderBy(i => i.ProductId))
            await Stock.RecordAsync(_db, item.ProductId, StockMovementTypes.Sale, -item.Quantity, _actor, now, sale.Id, shiftId);

        await Ledger.AppendAsync(_db, new LedgerEntryEntity
        {
            UserId = userId, Type = LedgerTypes.BarSale, Method = method, Amount = total,
            BalanceDelta = method == PaymentMethods.Balance ? -total : 0m,
            DeskAmount = PaymentMethods.IsDesk(method) ? total : 0m,
            SaleId = sale.Id, ShiftId = shiftId,
            Description = $"Бар, чек №{sale.Id}: {ItemsText(items)} — {total} {cur}"
        }, _actor, now);
        await tx.CommitAsync();
        return BillingResult<SaleEntity>.Success(sale);
    }

    public async Task<BillingResult<SaleEntity>> RefundAsync(long saleId, string? reason, bool returnToStock = true)
    {
        var cur = await _db.CurrencySymbolAsync();
        var why = reason?.Trim();
        if (why is not { Length: >= 3 }) return BillingErrors.ReasonRequired;
        if (why.Length > 300) why = why[..300];
        var sale = await _db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == saleId);
        if (sale == null) return PosErrors.SaleNotFound;
        if (sale.Kind != SaleKinds.Sale) return PosErrors.NotASale;
        if (await _db.Sales.AnyAsync(s => s.RefundOfSaleId == saleId)) return PosErrors.AlreadyRefunded;
        var desk = PaymentMethods.IsDesk(sale.Method);
        if (desk && _db.RequireOpenShift && await ShiftService.OpenShiftAsync(_db) == null) return ShiftErrors.Required;
        var items = await _db.SaleItems.AsNoTracking().Where(i => i.SaleId == saleId).OrderBy(i => i.Id).ToListAsync();
        var ids = items.Select(i => i.ProductId).ToList();
        var tracked = (await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrackStock).Select(p => p.Id).ToListAsync()).ToHashSet();

        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (sale.UserId.HasValue)
        {
            if (sale.Method == PaymentMethods.Balance) await Wallet.CreditAsync(_db, sale.UserId.Value, sale.Total);
            else await LockPlayerAsync(sale.UserId.Value);
        }
        var back = returnToStock ? items.Where(i => tracked.Contains(i.ProductId)).OrderBy(i => i.ProductId).ToList() : new List<SaleItemEntity>();
        foreach (var item in back) await Stock.AddAsync(_db, item.ProductId, item.Quantity);

        var shiftId = await ShiftService.LockOpenShiftIdAsync(_db);
        if (sale.Method == PaymentMethods.Cash && shiftId.HasValue)
        {
            var shift = await _db.Shifts.AsNoTracking().FirstAsync(s => s.Id == shiftId.Value);
            var inDrawer = await ShiftService.ExpectedCashAsync(_db, shift);
            if (sale.Total > inDrawer) return ShiftErrors.CashTooLow.With(inDrawer);
        }
        var refund = new SaleEntity
        {
            CreatedUtc = now, Kind = SaleKinds.Refund, RefundOfSaleId = sale.Id, ShiftId = shiftId, UserId = sale.UserId,
            Method = sale.Method, Total = sale.Total, Reason = why, ActorId = _actor?.Id, ActorUsername = _actor?.Username
        };
        _db.Sales.Add(refund);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return PosErrors.AlreadyRefunded; // refunded by someone else at the same moment (unique index)
        }
        _db.SaleItems.AddRange(items.Select(i => new SaleItemEntity
        {
            SaleId = refund.Id, ProductId = i.ProductId, Name = i.Name, Category = i.Category, UnitPrice = i.UnitPrice, Quantity = i.Quantity, LineTotal = i.LineTotal
        }));
        await _db.SaveChangesAsync();
        foreach (var item in back)
            await Stock.RecordAsync(_db, item.ProductId, StockMovementTypes.Refund, item.Quantity, _actor, now, refund.Id, shiftId, why);

        await Ledger.AppendAsync(_db, new LedgerEntryEntity
        {
            UserId = sale.UserId, Type = LedgerTypes.BarRefund, Method = sale.Method, Amount = sale.Total,
            BalanceDelta = sale.Method == PaymentMethods.Balance ? sale.Total : 0m,
            DeskAmount = desk ? -sale.Total : 0m,
            SaleId = refund.Id, ShiftId = shiftId, Reason = why,
            Description = $"Повернення чека №{sale.Id} ({ItemsText(items)}) — {sale.Total} {cur}: {why}"
        }, _actor, now);
        if (_audit != null)
        {
            await _audit.WriteAsync(AuditActions.SaleRefunded, "sale", sale.Id,
                $"Bar receipt #{sale.Id} refunded: {sale.Total} {cur} ({sale.Method}): {why}",
                new { sale = sale.Id, refund = refund.Id, total = sale.Total, method = sale.Method, soldBy = sale.ActorUsername, reason = why, returnToStock,
                      items = items.Select(i => $"{i.Name} x{i.Quantity}") });
        }
        await tx.CommitAsync();
        return BillingResult<SaleEntity>.Success(refund);
    }

    private Task LockPlayerAsync(Guid userId) =>
        _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Balance, u => u.Balance));

    public static string ItemsText(IEnumerable<SaleItemEntity> items) =>
        string.Join(", ", items.Select(i => i.Quantity > 1 ? $"{i.Name} ×{i.Quantity}" : i.Name));
}

public static class PosErrors
{
    private const int NotFound = StatusCodes.Status404NotFound;
    private const int Conflict = StatusCodes.Status409Conflict;

    public static readonly BillingError EmptySale = new("empty_sale", "Add at least one product.");
    public static readonly BillingError BadQuantity = new("bad_quantity", "Quantity must be between 1 and 100 (at most 50 lines).");
    public static readonly BillingError ProductNotFound = new("product_not_found", "Product not found", NotFound);
    public static readonly BillingError ProductInactive = new("product_inactive", "{0} is not on sale.", Conflict);
    public static readonly BillingError OutOfStock = new("out_of_stock", "Not enough {0} in stock: {1} left.", Conflict);
    public static readonly BillingError InsufficientBalance = new("insufficient_balance_bar", "Not enough real balance: need {0}, have {1} (bonus pays only for PC time).", Conflict);
    public static readonly BillingError SaleNotFound = new("sale_not_found", "Receipt not found", NotFound);
    public static readonly BillingError NotASale = new("not_a_sale", "This receipt is a refund; it cannot be refunded.", Conflict);
    public static readonly BillingError AlreadyRefunded = new("sale_already_refunded", "This receipt is already refunded.", Conflict);
    public static readonly BillingError BadProduct = new("bad_product", "Check the product fields: name, category, price above 0.");
    public static readonly BillingError ProductInUse = new("product_in_use", "The product has sales or stock history. Take it off sale instead.", Conflict);
    public static readonly BillingError StockNotTracked = new("stock_not_tracked", "Stock is not counted for this product.", Conflict);
    public static readonly BillingError BadStockQuantity = new("bad_stock_quantity", "The quantity must be between 0 and 100000 (restock: at least 1).");
}
