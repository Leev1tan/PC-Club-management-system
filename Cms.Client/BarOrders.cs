using Cms.Shared;

namespace Cms.Client;

/// <summary>The bar menu of the PC (GET /api/devices/{id}/bar-menu through the agent).</summary>
public sealed class BarMenu
{
    public bool Available { get; init; }
    public string Currency { get; init; } = ClientCurrencies.Default;
    public string CurrencySymbol { get; init; } = "₴";
    public decimal MaxAmount { get; init; }
    public int MaxLines { get; init; } = 10;
    public int MaxQuantity { get; init; } = 10;
    public bool CanPayFromBalance { get; init; }
    public decimal? Balance { get; init; }
    public bool HasOpenOrder { get; init; }
    public IReadOnlyList<BarMenuProduct> Products { get; init; } = Array.Empty<BarMenuProduct>();
}

public sealed record BarMenuProduct(Guid Id, string Name, string Category, decimal Price, int MaxQuantity);

/// <summary>Why "+" did not add one more of a product (Windows test 2026-11-27, defect 9: every refusal said "at most 500 ₴").</summary>
public enum BarCartLimit
{
    None,
    /// <summary>The product is not on the menu (any more).</summary>
    UnknownProduct,
    /// <summary>The order has the most different products (MaxLines).</summary>
    Lines,
    /// <summary>No more of this product: its stock, or the most of one product per order.</summary>
    Product,
    /// <summary>The order would cost more than the most per order (MaxAmount).</summary>
    Amount
}

public static class OrderPayments
{
    public const string Balance = "balance";
    public const string Desk = "desk";
}

/// <summary>
/// The cart of a bar order on the lock screen: the limits of the menu (lines, quantity per product, stock, the most
/// per order) are checked here before the request goes to the agent; the server checks them again.
/// </summary>
public sealed class BarCart
{
    private readonly BarMenu _menu;
    private readonly Dictionary<Guid, int> _quantities = new();

    public BarCart(BarMenu menu) => _menu = menu;

    public IReadOnlyDictionary<Guid, int> Quantities => _quantities;
    public int Count(Guid productId) => _quantities.TryGetValue(productId, out var q) ? q : 0;

    public decimal Total => _quantities.Sum(kv => (_menu.Products.FirstOrDefault(p => p.Id == kv.Key)?.Price ?? 0m) * kv.Value);

    public bool IsEmpty => _quantities.Count == 0;

    /// <summary>One more of the product; false when a limit stops it (stock, 10 of one, 10 lines, the most per order).</summary>
    public bool Add(Guid productId) => TryAdd(productId) == BarCartLimit.None;

    /// <summary>One more of the product, or the limit that stops it (BarCartLimit.None when it was added).</summary>
    public BarCartLimit TryAdd(Guid productId)
    {
        var product = _menu.Products.FirstOrDefault(p => p.Id == productId);
        if (product == null) return BarCartLimit.UnknownProduct;
        var now = Count(productId);
        if (now == 0 && _quantities.Count >= _menu.MaxLines) return BarCartLimit.Lines;
        if (now + 1 > Math.Min(product.MaxQuantity, _menu.MaxQuantity)) return BarCartLimit.Product;
        if (_menu.MaxAmount > 0 && Total + product.Price > _menu.MaxAmount) return BarCartLimit.Amount;
        _quantities[productId] = now + 1;
        return BarCartLimit.None;
    }

    public void Remove(Guid productId)
    {
        var now = Count(productId);
        if (now <= 1) _quantities.Remove(productId);
        else _quantities[productId] = now - 1;
    }

    public void Clear() => _quantities.Clear();

    /// <summary>Payment from the balance: a player's session with enough real balance (bonus never pays at the bar).</summary>
    public bool CanPayFromBalance => _menu.CanPayFromBalance && _menu.Balance is { } b && b >= Total && !IsEmpty;

    public PipeRequest ToRequest(string payment) =>
        new(PipeOps.Order, Items: _quantities.Select(kv => new PipeOrderItem(kv.Key, kv.Value)).ToList(), Payment: payment);
}

/// <summary>The line of the session screen about the bar order of the session.</summary>
public static class OrderNotice
{
    public static string? Line(ClientOrderInfo? order, LauncherTexts t, ClientSettings settings)
    {
        if (order == null) return null;
        var status = order.Status switch
        {
            "new" => t.OrderStatusNew,
            "preparing" => t.OrderStatusPreparing,
            "delivered" => t.OrderStatusDelivered,
            "cancelled" => t.OrderStatusCancelled,
            _ => order.Status
        };
        var paid = order.Payment == OrderPayments.Balance ? t.OrderPaidBalance : t.OrderPayAtDesk;
        return LauncherTexts.Format(t.OrderLine, order.Id, status, LauncherFormat.Money(order.Total, settings), paid);
    }

    /// <summary>The player may cancel only a new order (the bar has not started it).</summary>
    public static bool CanCancel(ClientOrderInfo? order) => order?.Status == "new";

    /// <summary>The text for a refused "+" (null when it was added).</summary>
    public static string? LimitText(BarCartLimit limit, BarMenu menu, LauncherTexts t, ClientSettings settings) => limit switch
    {
        BarCartLimit.None => null,
        BarCartLimit.Lines => LauncherTexts.Format(t.OrderMaxLines, menu.MaxLines),
        BarCartLimit.Product => t.OrderMaxProduct,
        BarCartLimit.Amount => LauncherTexts.Format(t.OrderMax, LauncherFormat.Money(menu.MaxAmount, settings)),
        _ => t.OrderSoldOut
    };
}
