using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Pos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>Bar checkout and the receipt log. Rules in Pos/SaleService. Refunds: Owner and Manager.</summary>
[ApiController]
[Route("api/sales")]
[Authorize(Policy = Policies.FrontDesk)]
public class SalesController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger? _audit;
    private readonly ClubClock _clock;

    public SalesController(CmsDbContext db, AuditLogger? audit = null, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private SaleService Service => new(_db, _clock, _audit, StaffActor.From(HttpContext?.User));

    /// <summary>
    /// Sell products: paymentMethod cash | card (at the desk, need an open shift) or balance (the
    /// player's real balance; default when userId is given). Tracked products need enough stock.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SaleView>> Sell([FromBody] SaleRequest req)
    {
        var result = await Service.SellAsync(req.Items?.Select(i => new SaleLine(i.ProductId, i.Quantity)).ToList(), req.PaymentMethod, req.UserId);
        return result.Ok ? Ok((await ViewsAsync(new[] { result.Value! })).Single()) : result.Error!.ToActionResult();
    }

    /// <summary>Refund a receipt the way it was paid (Owner, Manager), with a reason; goods back to stock unless returnToStock = false.</summary>
    [HttpPost("{id:long}/refund")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<SaleView>> Refund(long id, [FromBody] SaleRefundRequest req)
    {
        var result = await Service.RefundAsync(id, req.Reason, req.ReturnToStock ?? true);
        return result.Ok ? Ok((await ViewsAsync(new[] { result.Value! })).Single()) : result.Error!.ToActionResult();
    }

    /// <summary>Receipts, newest first; filters: shiftId, userId; paging with beforeId.</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SaleView>>> List([FromQuery] long? shiftId = null, [FromQuery] Guid? userId = null,
        [FromQuery] int take = 50, [FromQuery] long? beforeId = null)
    {
        var q = _db.Sales.AsNoTracking().AsQueryable();
        if (shiftId.HasValue) q = q.Where(s => s.ShiftId == shiftId);
        if (userId.HasValue) q = q.Where(s => s.UserId == userId);
        if (beforeId.HasValue) q = q.Where(s => s.Id < beforeId.Value);
        var sales = await q.OrderByDescending(s => s.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        return Ok(await ViewsAsync(sales));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<SaleView>> Get(long id)
    {
        var sale = await _db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        return sale == null ? NotFound() : Ok((await ViewsAsync(new[] { sale })).Single());
    }

    private async Task<List<SaleView>> ViewsAsync(IReadOnlyList<SaleEntity> sales)
    {
        var ids = sales.Select(s => s.Id).ToList();
        var items = (await _db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).ToListAsync())
            .GroupBy(i => i.SaleId).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id).ToList());
        var refunds = await _db.Sales.AsNoTracking().Where(s => s.RefundOfSaleId != null && ids.Contains(s.RefundOfSaleId.Value))
            .ToDictionaryAsync(s => s.RefundOfSaleId!.Value, s => s.Id);
        var userIds = sales.Where(s => s.UserId.HasValue).Select(s => s.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username);
        return sales.Select(s => new SaleView(
            s.Id, s.CreatedUtc, s.Kind, s.RefundOfSaleId, refunds.TryGetValue(s.Id, out var r) ? r : null, s.ShiftId,
            s.UserId, s.UserId.HasValue && users.TryGetValue(s.UserId.Value, out var name) ? name : null,
            s.Method, s.Total, s.Reason, s.ActorUsername,
            (items.TryGetValue(s.Id, out var list) ? list : new()).Select(i => new SaleItemView(i.ProductId, i.Name, i.Category, i.UnitPrice, i.Quantity, i.LineTotal)).ToList()
        )).ToList();
    }
}

public record SaleItemRequest(Guid ProductId, int Quantity);
public record SaleRequest(List<SaleItemRequest>? Items, string? PaymentMethod = null, Guid? UserId = null);
public record SaleRefundRequest(string? Reason, bool? ReturnToStock = true);

public record SaleItemView(Guid ProductId, string Name, string Category, decimal UnitPrice, int Quantity, decimal LineTotal);
/// <summary>Kind sale or refund; RefundedBySaleId: the refund receipt of a refunded sale.</summary>
public record SaleView(long Id, DateTimeOffset CreatedUtc, string Kind, long? RefundOfSaleId, long? RefundedBySaleId, long? ShiftId,
    Guid? UserId, string? Username, string Method, decimal Total, string? Reason, string? ActorUsername, IReadOnlyList<SaleItemView> Items);
