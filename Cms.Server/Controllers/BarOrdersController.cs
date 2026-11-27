using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Pos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Controllers;

/// <summary>
/// Bar orders, staff side (front desk: Owner, Manager, Cashier): the queue (New, Preparing, Delivered, Cancelled),
/// preparing, delivered (the bar sale with its payment method), cancel (a prepaid order back to the balance). The
/// settings (on/off, most per order) need Owner or Manager.
/// </summary>
[ApiController]
[Route("api/orders")]
[Authorize(Policy = Policies.FrontDesk)]
public class BarOrdersController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly AuditLogger _audit;
    private readonly ClubClock _clock;

    public BarOrdersController(CmsDbContext db, AuditLogger audit, ClubClock? clock = null)
    {
        _db = db;
        _audit = audit;
        _clock = clock ?? ClubClock.System();
    }

    private BarOrderService Service => new(_db, _clock, _audit, StaffActor.From(HttpContext?.User));

    /// <summary>Open orders (New, Preparing) and the last 30 closed ones; the badge counts the New ones.</summary>
    [HttpGet]
    public async Task<ActionResult<OrderQueueView>> Queue()
    {
        var open = await _db.BarOrders.AsNoTracking().Where(o => o.Status == BarOrderStatuses.New || o.Status == BarOrderStatuses.Preparing).OrderBy(o => o.Id).ToListAsync();
        var closed = await _db.BarOrders.AsNoTracking().Where(o => o.Status == BarOrderStatuses.Delivered || o.Status == BarOrderStatuses.Cancelled)
            .OrderByDescending(o => o.Id).Take(30).ToListAsync();
        var views = await ViewsAsync(open.Concat(closed).ToList());
        return Ok(new OrderQueueView(open.Count(o => o.Status == BarOrderStatuses.New), views.Where(v => v.Status is BarOrderStatuses.New or BarOrderStatuses.Preparing).ToList(),
            views.Where(v => v.Status is BarOrderStatuses.Delivered or BarOrderStatuses.Cancelled).ToList()));
    }

    private async Task<List<BarOrderView>> ViewsAsync(IReadOnlyList<BarOrderEntity> orders)
    {
        var ids = orders.Select(o => o.Id).ToList();
        var items = (await _db.BarOrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).ToListAsync()).ToLookup(i => i.OrderId);
        var deviceIds = orders.Select(o => o.DeviceId).Distinct().ToList();
        var hosts = await _db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Hostname);
        var userIds = orders.Where(o => o.UserId.HasValue).Select(o => o.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username);
        return orders.Select(o => new BarOrderView(o.Id, o.Status, o.Payment, o.DeskMethod, o.Total, o.DeviceId, hosts.GetValueOrDefault(o.DeviceId) ?? "?",
            o.UserId.HasValue ? users.GetValueOrDefault(o.UserId.Value) : null, o.CreatedUtc, o.UpdatedUtc, o.DeliveredUtc, o.SaleId, o.CancelReason, o.HandledByUsername,
            items[o.Id].OrderBy(i => i.Id).Select(i => new BarOrderItemView(i.Name, i.Quantity, i.UnitPrice, i.LineTotal)).ToList())).ToList();
    }

    private async Task<ActionResult<BarOrderView>> ResultAsync(BillingResult<BarOrderEntity> result) =>
        result.Ok ? Ok((await ViewsAsync(new[] { result.Value! })).Single()) : result.Error!.ToActionResult();

    [HttpPost("{id:long}/preparing")]
    public async Task<ActionResult<BarOrderView>> Preparing(long id) => await ResultAsync(await Service.PrepareAsync(id));

    /// <summary>Delivered: the bar sale is recorded now. Desk orders need paymentMethod cash or card (and an open shift).</summary>
    [HttpPost("{id:long}/deliver")]
    public async Task<ActionResult<BarOrderView>> Deliver(long id, [FromBody] DeliverOrderRequest? req) => await ResultAsync(await Service.DeliverAsync(id, req?.PaymentMethod));

    [HttpPost("{id:long}/cancel")]
    public async Task<ActionResult<BarOrderView>> Cancel(long id, [FromBody] Cms.Server.Reservations.ReasonRequest? req) => await ResultAsync(await Service.CancelAsync(id, req?.Reason));

    [HttpGet("settings")]
    public async Task<ActionResult<BarOrderSettings>> GetSettings() => Ok(await BarOrderSettings.LoadAsync(_db));

    [HttpPut("settings")]
    [Authorize(Policy = Policies.Management)]
    public async Task<ActionResult<BarOrderSettings>> PutSettings([FromBody] BarOrderSettings settings)
    {
        if (settings.Validate() is { } error) return BarOrderErrors.BadSettings.With(error).ToActionResult();
        var before = await BarOrderSettings.SaveAsync(_db, settings, User.Identity?.Name, _clock.UtcNow);
        if (before != settings)
            await _audit.WriteAsync(AuditActions.OrderSettingsChanged, "setting", ClubSettingKeys.BarOrders,
                $"Bar order settings changed: {(before.Enabled ? "on" : "off")} -> {(settings.Enabled ? "on" : "off")}, at most {before.MaxAmount} -> {settings.MaxAmount} {await _db.CurrencySymbolAsync()}",
                new { before, after = settings });
        return Ok(settings);
    }
}

/// <summary>Agent of an approved PC, for its own PC: the menu, a new order, the player's cancel of a new order.</summary>
[ApiController]
[Route("api/devices/{deviceId:guid}")]
[Authorize(Policy = Policies.ApprovedDevice)]
public class DeviceBarOrdersController : ControllerBase
{
    private readonly CmsDbContext _db;
    private readonly ClubClock _clock;

    public DeviceBarOrdersController(CmsDbContext db, ClubClock? clock = null)
    {
        _db = db;
        _clock = clock ?? ClubClock.System();
    }

    // Audited without an HTTP actor (the order comes from the PC)
    private BarOrderService Service => new(_db, _clock, new AuditLogger(_db, new HttpContextAccessor()));

    [HttpGet("bar-menu")]
    public async Task<ActionResult<BarMenuView>> Menu(Guid deviceId) =>
        User.DeviceId() != deviceId ? Forbid() : Ok(await Service.MenuAsync(deviceId));

    [HttpPost("orders")]
    public async Task<IActionResult> Place(Guid deviceId, [FromBody] PlaceOrderRequest req)
    {
        if (User.DeviceId() != deviceId) return Forbid();
        var result = await Service.PlaceAsync(deviceId, req.Items?.Select(i => new OrderLine(i.ProductId, i.Quantity)).ToList(), req.Payment);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok(new { orderId = result.Value!.Id, status = result.Value.Status, total = result.Value.Total, payment = result.Value.Payment });
    }

    [HttpPost("orders/cancel")]
    public async Task<IActionResult> Cancel(Guid deviceId)
    {
        if (User.DeviceId() != deviceId) return Forbid();
        var service = Service;
        var open = await service.OpenOrderAsync(deviceId);
        if (open == null) return BarOrderErrors.NotFound.ToActionResult();
        var result = await service.CancelAsync(open.Id, null, byPlayer: true);
        if (!result.Ok) return result.Error!.ToActionResult();
        return Ok(new { orderId = open.Id, status = result.Value!.Status });
    }
}

public record PlaceOrderItem(Guid ProductId, int Quantity);
/// <summary>Payment: balance (the player's real balance, now) or desk (cash or card at the delivery).</summary>
public record PlaceOrderRequest(IReadOnlyList<PlaceOrderItem>? Items, string? Payment);
public record DeliverOrderRequest(string? PaymentMethod);
public record BarOrderItemView(string Name, int Quantity, decimal UnitPrice, decimal LineTotal);
public record BarOrderView(long Id, string Status, string Payment, string? DeskMethod, decimal Total, Guid DeviceId, string Hostname, string? Username,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, DateTimeOffset? DeliveredUtc, long? SaleId, string? CancelReason, string? HandledBy,
    IReadOnlyList<BarOrderItemView> Items);
public record OrderQueueView(int NewCount, IReadOnlyList<BarOrderView> Open, IReadOnlyList<BarOrderView> Recent);
