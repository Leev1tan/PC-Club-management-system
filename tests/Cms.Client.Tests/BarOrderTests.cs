using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

public sealed class BarOrderTests
{
    private static readonly Guid Cola = Guid.NewGuid(), Chips = Guid.NewGuid(), Coffee = Guid.NewGuid();

    private static BarMenu Menu(decimal? balance = 100m, bool player = true, decimal max = 200m) => new()
    {
        Available = true, MaxAmount = max, MaxLines = 2, MaxQuantity = 10, CanPayFromBalance = player, Balance = balance,
        Products = new[] { new BarMenuProduct(Cola, "Кола", "Напої", 45m, 3), new BarMenuProduct(Chips, "Чипси", "Снеки", 60m, 10), new BarMenuProduct(Coffee, "Кава", "Кава", 50m, 10) }
    };

    [Fact]
    public void The_cart_keeps_the_limits_of_the_menu()
    {
        var cart = new BarCart(Menu());
        Assert.True(cart.Add(Cola) && cart.Add(Cola) && cart.Add(Cola));
        Assert.False(cart.Add(Cola));                 // stock: 3
        Assert.True(cart.Add(Chips));                 // 135 + 60 = 195
        Assert.False(cart.Add(Coffee));               // 2 lines at most (and 245 > 200)
        Assert.False(cart.Add(Chips));                // 255 > 200
        Assert.Equal(195m, cart.Total);
        cart.Remove(Cola);
        Assert.Equal((2, 150m), (cart.Count(Cola), cart.Total));
        Assert.False(cart.CanPayFromBalance);         // 150 > 100 of the real balance
        cart.Remove(Chips);
        Assert.True(cart.CanPayFromBalance);
        var req = cart.ToRequest(OrderPayments.Balance);
        Assert.Equal((PipeOps.Order, "balance"), (req.Op, req.Payment));
        Assert.Equal(new PipeOrderItem(Cola, 2), Assert.Single(req.Items!));
        Assert.False(new BarCart(Menu(player: false, balance: null)).CanPayFromBalance);
    }

    [Fact]
    public void The_session_screen_tells_the_state_of_the_order_in_the_club_language()
    {
        var order = new ClientOrderInfo { Id = 7, Status = "preparing", Payment = "balance", Total = 95m, Items = 2 };
        var uah = ClientSettings.Default;
        Assert.Equal("Order #7: being prepared · 95.00 ₴ · paid from the balance", OrderNotice.Line(order, LauncherTexts.En, uah));
        Assert.Equal("Замовлення №7: готується · 95.00 ₴ · оплачено з балансу", OrderNotice.Line(order, LauncherTexts.Uk, uah));
        Assert.Null(OrderNotice.Line(null, LauncherTexts.En, uah));
        Assert.False(OrderNotice.CanCancel(order));
        Assert.True(OrderNotice.CanCancel(new ClientOrderInfo { Status = "new" }));
    }

    [Fact]
    public void Order_errors_have_texts_and_the_pipe_takes_the_new_operations()
    {
        Assert.Equal(LauncherTexts.En.OrderOneOpen, LoginMessages.For(new PipeResponse(409, "{\"code\":\"order_already_open\"}"), LauncherTexts.En));
        Assert.Equal("At most 500.00 ₴ per order", LoginMessages.For(new PipeResponse(400, "{\"code\":\"order_too_much\",\"args\":[\"500.00 ₴\"]}"), LauncherTexts.En));
        Assert.Equal(LauncherTexts.Uk.OrderBalanceLow, LoginMessages.For(new PipeResponse(409, "{\"code\":\"insufficient_balance_bar\"}"), LauncherTexts.Uk));
        var line = System.Text.Json.JsonSerializer.Serialize(new PipeRequest(PipeOps.Order, Items: new[] { new PipeOrderItem(Coffee, 2) }, Payment: "desk"), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var parsed = AgentPipe.ParseRequest(line)!;
        Assert.Equal((PipeOps.Order, "desk", 2), (parsed.Op, parsed.Payment, parsed.Items![0].Quantity));
        Assert.NotNull(AgentPipe.ParseRequest("{\"op\":\"menu\"}"));
        Assert.NotNull(AgentPipe.ParseRequest("{\"op\":\"order_cancel\"}"));
    }

    /// <summary>Windows test 2026-11-27, defect 9: "+" at the stock limit said "at most 500.00 ₴ per order" (the total was 90).</summary>
    [Fact]
    public void A_refused_plus_names_the_limit_that_stopped_it()
    {
        var menu = Menu(max: 500m);
        var cart = new BarCart(menu);
        var uah = ClientSettings.Default;
        Assert.Equal(BarCartLimit.None, cart.TryAdd(Cola));
        Assert.Equal(BarCartLimit.None, cart.TryAdd(Cola));
        Assert.Equal(BarCartLimit.None, cart.TryAdd(Cola));
        Assert.Equal(BarCartLimit.Product, cart.TryAdd(Cola));                        // stock 3, total 135
        Assert.Equal("No more of this product in one order", OrderNotice.LimitText(BarCartLimit.Product, menu, LauncherTexts.En, uah));
        Assert.Equal("Більше цього товару в одному замовленні не можна", OrderNotice.LimitText(BarCartLimit.Product, menu, LauncherTexts.Uk, uah));
        Assert.Equal(BarCartLimit.None, cart.TryAdd(Chips));
        Assert.Equal(BarCartLimit.Lines, cart.TryAdd(Coffee));                         // 2 different products at most
        Assert.Equal("At most 2 different products per order", OrderNotice.LimitText(BarCartLimit.Lines, menu, LauncherTexts.En, uah));
        Assert.Equal(BarCartLimit.UnknownProduct, cart.TryAdd(Guid.NewGuid()));
        Assert.Equal(LauncherTexts.En.OrderSoldOut, OrderNotice.LimitText(BarCartLimit.UnknownProduct, menu, LauncherTexts.En, uah));
        Assert.Null(OrderNotice.LimitText(BarCartLimit.None, menu, LauncherTexts.En, uah));

        var small = new BarCart(Menu(max: 100m));
        Assert.Equal(BarCartLimit.None, small.TryAdd(Cola));
        Assert.Equal(BarCartLimit.None, small.TryAdd(Cola));                           // 90
        Assert.Equal(BarCartLimit.Amount, small.TryAdd(Cola));                         // 135 > 100 (the stock allows 3)
        Assert.Equal(BarCartLimit.Amount, small.TryAdd(Chips));                        // 150 > 100
        Assert.Equal("At most 100.00 ₴ per order", OrderNotice.LimitText(BarCartLimit.Amount, Menu(max: 100m), LauncherTexts.En, uah));
    }
}
