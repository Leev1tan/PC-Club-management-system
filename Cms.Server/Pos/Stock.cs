using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Pos;

/// <summary>
/// Atomic stock changes. Products.StockQuantity is the cached sum of the StockMovements rows: each
/// change is ONE conditional UPDATE (never below zero) plus a movement row in the same transaction,
/// like the player balances and the ledger (Billing/Wallet).
/// </summary>
public static class Stock
{
    /// <summary>Takes the quantity of a tracked product; false when there is not enough.</summary>
    public static async Task<bool> TryTakeAsync(CmsDbContext db, Guid productId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var rows = await db.Products
            .Where(p => p.Id == productId && p.TrackStock && p.StockQuantity >= quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity - quantity), ct);
        return rows == 1;
    }

    /// <summary>Adds to the stock (restock, refund).</summary>
    public static async Task AddAsync(CmsDbContext db, Guid productId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        await db.Products.Where(p => p.Id == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity + quantity), ct);
    }

    /// <summary>Sets a counted quantity (correction); returns the quantity before. Locks the product row.</summary>
    public static async Task<int> SetAsync(CmsDbContext db, Guid productId, int counted, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"Products\" WHERE \"Id\" = {0} FOR UPDATE", new object[] { productId }, ct);
        var before = await QuantityAsync(db, productId, ct);
        await db.Products.Where(p => p.Id == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, counted), ct);
        return before;
    }

    public static Task<int> QuantityAsync(CmsDbContext db, Guid productId, CancellationToken ct = default) =>
        db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.StockQuantity).FirstAsync(ct);

    /// <summary>Appends a movement row with the quantity after it (call after the change, same transaction).</summary>
    public static async Task<StockMovementEntity> RecordAsync(CmsDbContext db, Guid productId, string type, int delta, StaffActor? actor,
        DateTimeOffset now, long? saleId = null, long? shiftId = null, string? reason = null, decimal? unitCost = null, CancellationToken ct = default)
    {
        var movement = new StockMovementEntity
        {
            CreatedUtc = now, ProductId = productId, Type = type, Delta = delta, QuantityAfter = await QuantityAsync(db, productId, ct),
            SaleId = saleId, ShiftId = shiftId, Reason = reason, UnitCost = unitCost, ActorId = actor?.Id, ActorUsername = actor?.Username
        };
        db.StockMovements.Add(movement);
        await db.SaveChangesAsync(ct);
        return movement;
    }

    /// <summary>A tracked product whose cached stock differs from the sum of its movements.</summary>
    public sealed record Mismatch(Guid ProductId, string Name, int StockQuantity, int MovementSum);

    public static async Task<List<Mismatch>> ReconcileAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.TrackStock).ToListAsync(ct);
        var sums = (await db.StockMovements.AsNoTracking().Select(m => new { m.ProductId, m.Delta }).ToListAsync(ct))
            .GroupBy(m => m.ProductId).ToDictionary(g => g.Key, g => g.Sum(m => m.Delta));
        return products
            .Select(p => new Mismatch(p.Id, p.Name, p.StockQuantity, sums.TryGetValue(p.Id, out var sum) ? sum : 0))
            .Where(m => m.StockQuantity != m.MovementSum).ToList();
    }
}
