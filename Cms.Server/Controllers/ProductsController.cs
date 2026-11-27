using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Pos;
using Cms.Server.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Bar catalog and stock. Front desk reads the catalog and records restocks (goods delivered);
/// Owner and Manager change products and prices (audited) and correct the stock with a reason (audited).
/// </summary>
[ApiController]
[Route("api/products")]
[Authorize(Policy = Policies.FrontDesk)]
public class ProductsController : ControllerBase
{
    public const int MaxStock = 100_000;

    private readonly CmsDbContext _db;
    private readonly AuditLogger? _audit;
    private readonly ClubClock _clock;

    public ProductsController(CmsDbContext db, AuditLogger? audit = null, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private StaffActor? Actor => StaffActor.From(HttpContext?.User);

    private Task AuditAsync(string action, Guid id, string summary, object details) =>
        _audit?.WriteAsync(action, "product", id, summary, details) ?? Task.CompletedTask;

    /// <summary>Catalog by category; inactive products only with includeInactive (the editor).</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductView>>> List([FromQuery] bool includeInactive = false, [FromQuery] bool lowStockOnly = false)
    {
        var products = await _db.Products.AsNoTracking().Where(p => includeInactive || p.IsActive).ToListAsync();
        var views = products.OrderBy(p => p.Category).ThenBy(p => p.SortOrder).ThenBy(p => p.Name).Select(ProductView.From);
        return Ok(lowStockOnly ? views.Where(v => v.LowStock) : views);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProductView>> Get(Guid id)
    {
        var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return p == null ? NotFound() : Ok(ProductView.From(p));
    }

    [HttpPost]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<ProductView>> Create([FromBody] ProductRequest req)
    {
        if (!Valid(req) || req.InitialStock is < 0 or > MaxStock) return PosErrors.BadProduct.ToActionResult();
        var now = _clock.UtcNow;
        var product = new ProductEntity { Id = Guid.NewGuid(), CreatedUtc = now };
        Apply(product, req);
        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        if (product.TrackStock && req.InitialStock is > 0)
        {
            await Stock.AddAsync(_db, product.Id, req.InitialStock.Value);
            await Stock.RecordAsync(_db, product.Id, StockMovementTypes.Restock, req.InitialStock.Value, Actor, now,
                shiftId: await ShiftService.LockOpenShiftIdAsync(_db), reason: "Початковий залишок");
        }
        await AuditAsync(AuditActions.ProductCreated, product.Id, $"Product '{product.Name}' created: {product.Price} ₴ ({product.Category})", Details(product));
        await tx.CommitAsync();
        return Ok(await ViewAsync(product.Id));
    }

    /// <summary>Changes the product; the stock is changed only by restocks and corrections.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<ProductView>> Update(Guid id, [FromBody] ProductRequest req)
    {
        if (!Valid(req)) return PosErrors.BadProduct.ToActionResult();
        var product = await _db.Products.FindAsync(id);
        if (product == null) return PosErrors.ProductNotFound.ToActionResult();
        var before = Details(product);
        var oldPrice = product.Price;
        Apply(product, req);
        await _db.SaveChangesAsync();
        if (oldPrice != product.Price)
            await AuditAsync(AuditActions.ProductPriceChanged, id, $"Price of '{product.Name}' changed: {oldPrice} -> {product.Price} ₴", new { before, after = Details(product) });
        else
            await AuditAsync(AuditActions.ProductUpdated, id, $"Product '{product.Name}' changed", new { before, after = Details(product) });
        return Ok(await ViewAsync(id));
    }

    /// <summary>Deletes a product that was never sold or counted; otherwise take it off sale (isActive = false).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Management)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return PosErrors.ProductNotFound.ToActionResult();
        if (await _db.SaleItems.AnyAsync(i => i.ProductId == id) || await _db.StockMovements.AnyAsync(m => m.ProductId == id))
            return PosErrors.ProductInUse.ToActionResult();
        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        await AuditAsync(AuditActions.ProductDeleted, id, $"Product '{product.Name}' deleted ({product.Price} ₴)", Details(product));
        return NoContent();
    }

    /// <summary>Goods delivered (front desk): adds to the stock, with who / when / how many.</summary>
    [HttpPost("{id:guid}/restock")]
    public async Task<ActionResult<StockMovementView>> Restock(Guid id, [FromBody] RestockRequest req)
    {
        if (req.Quantity is < 1 or > MaxStock || req.UnitCost is < 0 or > 100_000) return PosErrors.BadStockQuantity.ToActionResult();
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (product == null) return PosErrors.ProductNotFound.ToActionResult();
        if (!product.TrackStock) return PosErrors.StockNotTracked.ToActionResult();
        var now = _clock.UtcNow;
        var note = Clean(req.Note);
        await using var tx = await _db.Database.BeginTransactionAsync();
        await Stock.AddAsync(_db, id, req.Quantity);
        var movement = await Stock.RecordAsync(_db, id, StockMovementTypes.Restock, req.Quantity, Actor, now,
            shiftId: await ShiftService.LockOpenShiftIdAsync(_db), reason: note, unitCost: req.UnitCost);
        await AuditAsync(AuditActions.StockRestocked, id, $"Restock of '{product.Name}': +{req.Quantity}, now {movement.QuantityAfter}",
            new { product = product.Name, quantity = req.Quantity, after = movement.QuantityAfter, unitCost = req.UnitCost, note });
        await tx.CommitAsync();
        return Ok(StockMovementView.From(movement, product.Name));
    }

    /// <summary>Stock count (Owner, Manager): sets the counted quantity, with a reason (audited).</summary>
    [HttpPost("{id:guid}/correction")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<StockMovementView>> Correct(Guid id, [FromBody] StockCorrectionRequest req)
    {
        if (req.CountedQuantity is < 0 or > MaxStock) return PosErrors.BadStockQuantity.ToActionResult();
        var reason = Clean(req.Reason);
        if (reason is not { Length: >= 3 }) return BillingErrors.ReasonRequired.ToActionResult();
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (product == null) return PosErrors.ProductNotFound.ToActionResult();
        if (!product.TrackStock) return PosErrors.StockNotTracked.ToActionResult();
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();
        var before = await Stock.SetAsync(_db, id, req.CountedQuantity);
        var delta = req.CountedQuantity - before;
        var movement = await Stock.RecordAsync(_db, id, StockMovementTypes.Correction, delta, Actor, now,
            shiftId: await ShiftService.LockOpenShiftIdAsync(_db), reason: reason);
        await AuditAsync(AuditActions.StockCorrected, id, $"Stock of '{product.Name}' corrected: {before} -> {req.CountedQuantity} ({delta:+0;-0;0}): {reason}",
            new { product = product.Name, before, counted = req.CountedQuantity, delta, reason });
        await tx.CommitAsync();
        return Ok(StockMovementView.From(movement, product.Name));
    }

    /// <summary>Stock log, newest first (filters: product, type); paging with beforeId.</summary>
    [HttpGet("movements")]
    public async Task<ActionResult<IEnumerable<StockMovementView>>> Movements([FromQuery] Guid? productId = null, [FromQuery] string? type = null,
        [FromQuery] int take = 100, [FromQuery] long? beforeId = null)
    {
        var q = _db.StockMovements.AsNoTracking().AsQueryable();
        if (productId.HasValue) q = q.Where(m => m.ProductId == productId);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(m => m.Type == type.Trim());
        if (beforeId.HasValue) q = q.Where(m => m.Id < beforeId.Value);
        var rows = await q.OrderByDescending(m => m.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        var ids = rows.Select(r => r.ProductId).Distinct().ToList();
        var names = await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        return Ok(rows.Select(r => StockMovementView.From(r, names.TryGetValue(r.ProductId, out var n) ? n : "?")));
    }

    /// <summary>Tracked products whose stock differs from the sum of the stock log (empty = all fine).</summary>
    [HttpGet("stock/check")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<StockCheckView>> Check()
    {
        var mismatches = await Stock.ReconcileAsync(_db);
        return Ok(new StockCheckView(mismatches.Count == 0, await _db.Products.CountAsync(p => p.TrackStock), await _db.StockMovements.CountAsync(), mismatches));
    }

    private async Task<ProductView> ViewAsync(Guid id) => ProductView.From(await _db.Products.AsNoTracking().FirstAsync(p => p.Id == id));

    private static bool Valid(ProductRequest req) =>
        !string.IsNullOrWhiteSpace(req.Name) && req.Name.Trim().Length <= 100 &&
        (req.Category ?? string.Empty).Trim().Length <= 50 &&
        req.Price > 0 && req.Price <= 100_000 && req.Price == TariffCalculator.Round(req.Price) &&
        req.LowStockThreshold is null or (>= 0 and <= MaxStock);

    private static void Apply(ProductEntity p, ProductRequest req)
    {
        p.Name = req.Name!.Trim();
        p.Category = string.IsNullOrWhiteSpace(req.Category) ? "Інше" : req.Category.Trim();
        p.Price = req.Price;
        p.IsActive = req.IsActive ?? p.IsActive;
        p.TrackStock = req.TrackStock ?? p.TrackStock;
        p.LowStockThreshold = req.LowStockThreshold;
        p.SortOrder = req.SortOrder ?? p.SortOrder;
    }

    private static object Details(ProductEntity p) => new { p.Name, p.Category, p.Price, p.IsActive, p.TrackStock, p.LowStockThreshold };

    private static string? Clean(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > 300 ? t[..300] : t;
    }
}

/// <summary>
/// IsActive / TrackStock / SortOrder: null keeps the value (new product: on sale, no stock count).
/// InitialStock: create only (a restock row "initial stock"); the stock changes later only by restocks and corrections.
/// </summary>
public record ProductRequest(string? Name, string? Category, decimal Price, bool? IsActive = null, bool? TrackStock = null,
    int? LowStockThreshold = null, int? SortOrder = null, int? InitialStock = null);
public record RestockRequest(int Quantity, decimal? UnitCost = null, string? Note = null);
public record StockCorrectionRequest(int CountedQuantity, string? Reason);

public record ProductView(Guid Id, string Name, string Category, decimal Price, bool IsActive, bool TrackStock, int StockQuantity,
    int? LowStockThreshold, bool LowStock, int SortOrder)
{
    public static ProductView From(ProductEntity p) => new(p.Id, p.Name, p.Category, p.Price, p.IsActive, p.TrackStock, p.StockQuantity,
        p.LowStockThreshold, p.TrackStock && p.LowStockThreshold.HasValue && p.StockQuantity <= p.LowStockThreshold.Value, p.SortOrder);
}

public record StockMovementView(long Id, DateTimeOffset CreatedUtc, Guid ProductId, string ProductName, string Type, int Delta, int QuantityAfter,
    long? SaleId, long? ShiftId, string? Reason, decimal? UnitCost, string? ActorUsername)
{
    public static StockMovementView From(StockMovementEntity m, string name) => new(m.Id, m.CreatedUtc, m.ProductId, name, m.Type, m.Delta,
        m.QuantityAfter, m.SaleId, m.ShiftId, m.Reason, m.UnitCost, m.ActorUsername);
}

public record StockCheckView(bool Ok, int TrackedProducts, int Movements, IReadOnlyList<Stock.Mismatch> Mismatches);
