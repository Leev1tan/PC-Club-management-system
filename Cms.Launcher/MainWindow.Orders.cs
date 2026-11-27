using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cms.Client;
using Cms.Shared;

namespace Cms.Launcher;

/// <summary>
/// Bar orders on the session screen: the ORDER button opens a panel with the bar menu of the agent (products in stock),
/// + / - per product, the payment (my real balance or at the desk) and the total. The cart and its limits are
/// Cms.Client.BarCart (tested); this part only draws it and sends the request through the agent pipe.
/// The state of the order comes back with the session in state.json (Cms.Client.OrderNotice).
/// </summary>
public partial class MainWindow
{
    private Border? _orderPanel;
    private StackPanel? _orderProducts;
    private TextBlock? _orderTitle, _orderTotal, _orderMessage;
    private RadioButton? _payBalance, _payDesk;
    private Button? _orderPlace, _orderClose, _orderCancel;
    private BarMenu? _menu;
    private BarCart? _cart;
    private bool _orderBusy;


    private async void OnOrderClick(object sender, RoutedEventArgs e)
    {
        EnsureOrderPanel();
        _orderPanel!.Visibility = Visibility.Visible;
        _orderMessage!.Text = string.Empty;
        await LoadMenuAsync();
    }

    private void EnsureOrderPanel()
    {
        if (_orderPanel != null) return;
        _orderTitle = new TextBlock { FontSize = 18, Margin = new Thickness(0, 0, 0, 12) }.Text("Brush.AccentText", "Font.Display");
        _orderProducts = new StackPanel();
        _orderTotal = new TextBlock { FontSize = 15, Margin = new Thickness(0, 10, 0, 6) }.Text("Brush.Warn", "Font.Mono");
        _payBalance = new RadioButton { GroupName = "orderpay", Margin = new Thickness(0, 2, 0, 2) }.Text("Brush.Text");
        _payDesk = new RadioButton { GroupName = "orderpay", Margin = new Thickness(0, 2, 0, 2), IsChecked = true }.Text("Brush.Text");
        _orderMessage = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }.Text("Brush.Danger");
        _orderPlace = new Button { Style = (Style)FindResource("NeonButton"), Margin = new Thickness(0, 4, 10, 0) };
        _orderCancel = new Button { Style = (Style)FindResource("CyberButton"), Margin = new Thickness(0, 4, 10, 0), Visibility = Visibility.Collapsed };
        _orderClose = new Button { Style = (Style)FindResource("CyberButton"), Margin = new Thickness(0, 4, 0, 0) };
        _orderPlace.Click += OnPlaceOrder;
        _orderCancel.Click += OnCancelOrder;
        _orderClose.Click += (_, _) => _orderPanel!.Visibility = Visibility.Collapsed;
        _orderPanel = new Border
        {
            BorderThickness = new Thickness(1), Padding = new Thickness(24), Width = 560, MaxHeight = 640,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Children =
                {
                    _orderTitle,
                    new ScrollViewer { MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _orderProducts },
                    _orderTotal, _payBalance, _payDesk, _orderMessage,
                    // A WrapPanel: with "[ CANCEL THE ORDER ]" the "[ CLOSE ]" button goes to the next line, never past the edge
                    new WrapPanel { Orientation = Orientation.Horizontal, Children = { _orderPlace, _orderCancel, _orderClose } }
                }
            }
        };
        _orderPanel.SetResourceReference(Border.BackgroundProperty, "Brush.BgPanel");
        _orderPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        Grid.SetRowSpan(_orderPanel, 5);
        Panel.SetZIndex(_orderPanel, 500);
        SessionPanel.Children.Add(_orderPanel);
        ApplyOrderTexts();
    }

    private void ApplyOrderTexts()
    {
        OrderButton.Content = _texts.OrderButton;
        if (_orderPanel == null) return;
        _orderTitle!.Text = _texts.OrderTitle;
        _payDesk!.Content = _texts.OrderPayDesk;
        _orderPlace!.Content = _texts.OrderPlace;
        _orderCancel!.Content = _texts.OrderCancel;
        _orderClose!.Content = _texts.OrderClose;
        RenderOrder();
    }

    private async Task LoadMenuAsync()
    {
        var res = await AskAgentAsync(new PipeRequest(PipeOps.Menu));
        _menu = res.Ok ? res.As<BarMenu>() : null;
        _cart = _menu is { Available: true } ? new BarCart(_menu) : null;
        if (!res.Ok) _orderMessage!.Text = LoginMessages.For(res, _texts);
        else if (_menu is not { Available: true }) _orderMessage!.Text = _texts.OrderNotAvailable;
        RenderOrder();
    }

    /// <summary>One row per product: name and price, then - quantity +; the total, the payment and the buttons.</summary>
    private void RenderOrder()
    {
        if (_orderPanel == null || _orderProducts == null) return;
        _orderProducts.Children.Clear();
        var settings = _menu != null ? new ClientSettings { Currency = _menu.Currency }.Normalized() : _settings;
        string Money(decimal amount) => LauncherFormat.Money(amount, settings);
        if (_menu != null && _cart != null)
        {
            if (_menu.Products.Count == 0)
                _orderProducts.Children.Add(new TextBlock { Text = _texts.OrderEmptyMenu }.Text("Brush.TextSecondary"));
            foreach (var p in _menu.Products)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                // The CyberButton padding (24,12) left no room for the sign in a narrow button: a small padding here
                var plus = new Button { Content = "+", Width = 40, Height = 34, Padding = new Thickness(0), FontSize = 18, Style = (Style)FindResource("CyberButton") };
                var minus = new Button { Content = "−", Width = 40, Height = 34, Padding = new Thickness(0), FontSize = 18, Style = (Style)FindResource("CyberButton") };
                var count = new TextBlock { Text = _cart.Count(p.Id).ToString(), Width = 34, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Text("Brush.Text", "Font.Mono");
                // The text says which limit stopped "+" (lines, this product, or the amount)
                plus.Click += (_, _) =>
                {
                    var limit = OrderNotice.LimitText(_cart.TryAdd(p.Id), _menu, _texts, settings);
                    _orderMessage!.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
                    _orderMessage.Text = limit ?? string.Empty;
                    RenderOrder();
                };
                minus.Click += (_, _) => { _cart.Remove(p.Id); RenderOrder(); };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { minus, count, plus } };
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
                row.Children.Add(new TextBlock { Text = $"{p.Name}  ·  {Money(p.Price)}", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }.Text("Brush.Text"));
                _orderProducts.Children.Add(row);
            }
        }
        var total = _cart?.Total ?? 0m;
        _orderTotal!.Text = LauncherTexts.Format(_texts.OrderTotal, Money(total));
        var canBalance = _cart?.CanPayFromBalance == true;
        _payBalance!.Content = LauncherTexts.Format(_texts.OrderPayBalance, Money(_menu?.Balance ?? 0m));
        _payBalance.Visibility = _menu?.CanPayFromBalance == true ? Visibility.Visible : Visibility.Collapsed;
        _payBalance.IsEnabled = canBalance;
        if (!canBalance) _payDesk!.IsChecked = true;
        _orderPlace!.IsEnabled = !_orderBusy && _cart is { IsEmpty: false } && _menu?.HasOpenOrder != true;
        _orderCancel!.Visibility = OrderNotice.CanCancel(_lastState?.Session?.Order) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPlaceOrder(object sender, RoutedEventArgs e)
    {
        if (_cart == null || _cart.IsEmpty || _orderBusy) return;
        _orderBusy = true;
        RenderOrder();
        try
        {
            var res = await AskAgentAsync(_cart.ToRequest(_payBalance?.IsChecked == true ? OrderPayments.Balance : OrderPayments.Desk));
            if (res.Ok)
            {
                _cart.Clear();
                _orderMessage!.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ok");
                _orderMessage.Text = _texts.OrderPlaced;
                await LoadMenuAsync();
            }
            else
            {
                _orderMessage!.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
                _orderMessage.Text = LoginMessages.For(res, _texts);
            }
        }
        finally
        {
            _orderBusy = false;
            RenderOrder();
        }
    }

    private async void OnCancelOrder(object sender, RoutedEventArgs e)
    {
        var res = await AskAgentAsync(new PipeRequest(PipeOps.OrderCancel));
        _orderMessage!.Text = res.Ok ? string.Empty : LoginMessages.For(res, _texts);
        await LoadMenuAsync();
    }

    /// <summary>The ORDER button during a running session (not paused) and the state of the order of the session.</summary>
    private void UpdateOrderStatus()
    {
        var session = _lastState?.Session;
        OrderButton.Visibility = session != null && !session.Paused ? Visibility.Visible : Visibility.Collapsed;
        var line = OrderNotice.Line(session?.Order, _texts, _settings);
        OrderStatusText.Text = line ?? string.Empty;
        OrderStatusText.Visibility = line != null ? Visibility.Visible : Visibility.Collapsed;
        if (session == null && _orderPanel != null) _orderPanel.Visibility = Visibility.Collapsed;
        if (_orderPanel is { Visibility: Visibility.Visible }) _orderCancel!.Visibility = OrderNotice.CanCancel(session?.Order) ? Visibility.Visible : Visibility.Collapsed;
    }
}
