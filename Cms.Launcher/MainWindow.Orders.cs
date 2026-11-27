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

    private static readonly SolidColorBrush OrderText = new(Color.FromRgb(232, 232, 240));
    private static readonly SolidColorBrush OrderMuted = new(Color.FromRgb(136, 136, 168));

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
        var mono = new FontFamily("Consolas");
        _orderTitle = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0, 240, 255)), FontFamily = mono, FontSize = 18, Margin = new Thickness(0, 0, 0, 12) };
        _orderProducts = new StackPanel();
        _orderTotal = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 224, 25)), FontFamily = mono, FontSize = 15, Margin = new Thickness(0, 10, 0, 6) };
        _payBalance = new RadioButton { GroupName = "orderpay", Foreground = OrderText, FontFamily = mono, Margin = new Thickness(0, 2, 0, 2) };
        _payDesk = new RadioButton { GroupName = "orderpay", Foreground = OrderText, FontFamily = mono, Margin = new Thickness(0, 2, 0, 2), IsChecked = true };
        _orderMessage = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 45, 120)), FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        _orderPlace = new Button { Style = (Style)FindResource("NeonButton"), Margin = new Thickness(0, 4, 10, 0) };
        _orderCancel = new Button { Style = (Style)FindResource("CyberButton"), Margin = new Thickness(0, 4, 10, 0), Visibility = Visibility.Collapsed };
        _orderClose = new Button { Style = (Style)FindResource("CyberButton"), Margin = new Thickness(0, 4, 0, 0) };
        _orderPlace.Click += OnPlaceOrder;
        _orderCancel.Click += OnCancelOrder;
        _orderClose.Click += (_, _) => _orderPanel!.Visibility = Visibility.Collapsed;
        _orderPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 13, 13, 21)), BorderBrush = new SolidColorBrush(Color.FromRgb(0, 240, 255)),
            BorderThickness = new Thickness(1), Padding = new Thickness(24), Width = 560, MaxHeight = 640,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Children =
                {
                    _orderTitle,
                    new ScrollViewer { MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _orderProducts },
                    _orderTotal, _payBalance, _payDesk, _orderMessage,
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { _orderPlace, _orderCancel, _orderClose } }
                }
            }
        };
        Grid.SetRowSpan(_orderPanel, 4);
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
                _orderProducts.Children.Add(new TextBlock { Text = _texts.OrderEmptyMenu, Foreground = OrderMuted, FontFamily = new FontFamily("Consolas") });
            foreach (var p in _menu.Products)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var plus = new Button { Content = "+", Width = 34, Style = (Style)FindResource("CyberButton") };
                var minus = new Button { Content = "−", Width = 34, Style = (Style)FindResource("CyberButton") };
                var count = new TextBlock { Text = _cart.Count(p.Id).ToString(), Width = 30, TextAlignment = TextAlignment.Center, Foreground = OrderText, VerticalAlignment = VerticalAlignment.Center };
                plus.Click += (_, _) => { if (!_cart.Add(p.Id)) _orderMessage!.Text = LauncherTexts.Format(_texts.OrderMax, Money(_menu.MaxAmount)); RenderOrder(); };
                minus.Click += (_, _) => { _cart.Remove(p.Id); RenderOrder(); };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { minus, count, plus } };
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
                row.Children.Add(new TextBlock { Text = $"{p.Name}  ·  {Money(p.Price)}", Foreground = OrderText, FontFamily = new FontFamily("Consolas"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
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
                _orderMessage!.Foreground = new SolidColorBrush(Color.FromRgb(57, 255, 20));
                _orderMessage.Text = _texts.OrderPlaced;
                await LoadMenuAsync();
            }
            else
            {
                _orderMessage!.Foreground = new SolidColorBrush(Color.FromRgb(255, 45, 120));
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
