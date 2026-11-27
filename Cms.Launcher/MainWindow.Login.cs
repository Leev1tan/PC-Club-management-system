using System.IO.Pipes;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;
using Cms.Client;
using Cms.Shared;

namespace Cms.Launcher;

/// <summary>
/// Player login on the lock screen and the session header. The lock screen cannot read the device key,
/// so every request goes to the agent through the local pipe (Cms.Client.AgentPipe), and only to a pipe
/// server whose process is the agent (its PID is in state.json, a file the player cannot write).
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan PipeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OffersMaxAge = TimeSpan.FromSeconds(60);

    private readonly LowTimeWarnings _warnings = new();
    private LoginOffers? _offers;
    private bool _offersFailed;
    private DateTimeOffset _offersLoadedUtc = DateTimeOffset.MinValue;
    private bool _offersLoading;
    private bool _loginBusy;
    private string _offerChoice = "balance";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    /// <summary>True when the pipe server is the agent process named in state.json.</summary>
    private bool VerifyAgentPipe(NamedPipeClientStream client)
    {
        try
        {
            var expected = _lastState?.AgentPid ?? 0;
            return expected > 0 && GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid) && pid == (uint)expected;
        }
        catch
        {
            return false;
        }
    }

    private Task<PipeResponse> AskAgentAsync(PipeRequest request) =>
        AgentPipeClient.SendAsync(AgentPipe.Name, request, PipeTimeout, VerifyAgentPipe);

    /// <summary>The login form shows while the PC is locked by the agent and has no session (not when the agent is silent).</summary>
    private void UpdateLoginPanel(LockDecision decision)
    {
        var state = _lastState;
        var visible = decision.Locked && decision.Reason == LockReason.LockedByAgent &&
                      state is { Enrollment: "approved", Session: null };
        if (!visible)
        {
            if (LoginPanel.Visibility == Visibility.Visible)
            {
                PasswordBox.Clear();
                UsernameBox.Clear();
                LoginError.Visibility = Visibility.Collapsed;
            }
            LoginPanel.Visibility = Visibility.Collapsed;
            return;
        }
        LoginPanel.Visibility = Visibility.Visible;
        if (DateTimeOffset.UtcNow - _offersLoadedUtc > OffersMaxAge) _ = RefreshOffersAsync();
    }

    private async Task RefreshOffersAsync()
    {
        if (_offersLoading) return;
        _offersLoading = true;
        try
        {
            var res = await AskAgentAsync(new PipeRequest(PipeOps.Offers));
            _offers = res.Ok ? res.As<LoginOffers>() : null;
            _offersFailed = _offers == null;
            _offersLoadedUtc = _offers != null ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow - OffersMaxAge + TimeSpan.FromSeconds(10);
        }
        finally
        {
            _offersLoading = false;
        }
        RenderOffers();
    }

    /// <summary>Radio buttons: from the balance (default), prepaid hours and the packages on sale, with prices in the club currency.</summary>
    private void RenderOffers()
    {
        OffersPanel.Children.Clear();
        var settings = _offers != null ? new ClientSettings { Currency = _offers.Currency }.Normalized() : _settings;
        string Money(decimal amount) => LauncherFormat.Money(amount, settings);

        void Add(string key, string text)
        {
            var radio = new RadioButton
            {
                Content = text, GroupName = "offer", Tag = key, IsChecked = key == _offerChoice,
                Foreground = new SolidColorBrush(Color.FromRgb(232, 232, 240)), FontFamily = new FontFamily("Consolas"), FontSize = 13,
                Margin = new Thickness(0, 2, 0, 2)
            };
            radio.Checked += (_, _) => _offerChoice = key;
            OffersPanel.Children.Add(radio);
        }

        Add("balance", _texts.OfferBalance);
        if (_offers != null)
        {
            foreach (var m in _offers.Minutes) Add($"m{m.Minutes}", LauncherTexts.Format(_texts.OfferMinutes, m.Minutes / 60, Money(m.Price)));
            foreach (var p in _offers.Packages)
                Add($"p{p.Id}", LauncherTexts.Format(_texts.OfferPackage, p.Name, Money(p.Price), p.EndsAtUtc.ToLocalTime().ToString("HH:mm")));
        }
        if (OffersPanel.Children.OfType<RadioButton>().All(r => r.IsChecked != true))
        {
            _offerChoice = "balance";
            ((RadioButton)OffersPanel.Children[0]).IsChecked = true;
        }
        OffersHintText.Text = _offers?.Tariff is { } t
            ? LauncherTexts.Format(_texts.OfferBalanceHint, t.Name, Money(t.PricePerHourNow))
            : _offersFailed ? _texts.OffersUnavailable : string.Empty;
    }

    private void ShowLoginError(string message, bool error = true)
    {
        LoginError.Text = message;
        LoginError.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 45, 120)) : new SolidColorBrush(Color.FromRgb(57, 255, 20));
        LoginError.Visibility = Visibility.Visible;
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs? e)
    {
        if (_loginBusy || LoginPanel.Visibility != Visibility.Visible) return;
        var username = UsernameBox.Text.Trim();
        var secret = PasswordBox.Password;
        if (username.Length == 0) { ShowLoginError(_texts.EnterUsername); return; }
        if (secret.Length == 0) { ShowLoginError(_texts.EnterSecret); return; }

        var (offer, minutes, packageId) = _offerChoice switch
        {
            var k when k.StartsWith('m') && int.TryParse(k[1..], out var m) => ("minutes", (int?)m, (Guid?)null),
            var k when k.StartsWith('p') && Guid.TryParse(k[1..], out var id) => ("package", null, id),
            _ => ("balance", null, null)
        };
        _loginBusy = true;
        LoginButton.IsEnabled = false;
        LoginButton.Content = _texts.LoginBusy;
        LoginError.Visibility = Visibility.Collapsed;
        try
        {
            var res = await AskAgentAsync(new PipeRequest(PipeOps.Login, username, secret, offer, minutes, packageId));
            PasswordBox.Clear();
            if (res.Ok)
            {
                ShowLoginError(_texts.LoginStarting, error: false); // the agent unlocks the PC with the next command
                _offersLoadedUtc = DateTimeOffset.MinValue;
            }
            else
            {
                ShowLoginError(LoginMessages.For(res, _texts));
                if (res.Code is "insufficient_balance" or "player_has_debt" or "package_outside_window" or "package_not_today") _ = RefreshOffersAsync();
            }
        }
        finally
        {
            _loginBusy = false;
            LoginButton.IsEnabled = true;
            LoginButton.Content = _texts.LoginButton;
        }
    }

    /// <summary>A player ends the own session (prepaid time is not refunded; time from the balance is paid).</summary>
    private async void OnEndSessionClick(object sender, RoutedEventArgs e)
    {
        var session = _lastState?.Session;
        if (session?.Username == null) return;
        var question = session.FromBalance || !session.Prepaid ? _texts.EndSessionBalance : _texts.EndSessionPrepaid;
        if (MessageBox.Show(this, question, _texts.EndSessionTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        EndSessionButton.IsEnabled = false;
        try
        {
            var res = await AskAgentAsync(new PipeRequest(PipeOps.Logout));
            if (!res.Ok)
                MessageBox.Show(this, LauncherTexts.Format(_texts.EndSessionFailed, LoginMessages.For(res, _texts)), _texts.EndSessionTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndSessionButton.IsEnabled = true;
        }
    }

    /// <summary>Player (or guest), balances, tariff or package of the active session, from state.json.</summary>
    private void UpdateSessionHeader()
    {
        var session = _lastState?.Session;
        var player = session?.Username != null;
        SessionUser.Text = "👤 " + (player ? session!.DisplayName ?? session.Username : _texts.Guest);
        SessionBalance.Text = player && session!.Balance is { } balance
            ? "💰 " + LauncherFormat.Money(balance, _settings) + (session.Bonus is > 0 ? " · " + LauncherTexts.Format(_texts.BonusLine, LauncherFormat.Money(session.Bonus.Value, _settings)) : "")
            : string.Empty;
        SessionTariff.Text = session?.Package is { } package ? LauncherTexts.Format(_texts.PackageLine, package)
            : session?.Tariff is { } tariff ? LauncherTexts.Format(_texts.TariffLine, tariff) : string.Empty;
        EndSessionButton.Visibility = player ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>At 10, 5 and 1 minute left: a sound and a small window on top of the game.</summary>
    private void AnnounceLowTime()
    {
        if (_warnings.Check(_lastState?.SessionEndUtc, DateTimeOffset.UtcNow) is not { } minutes) return;
        try { SystemSounds.Exclamation.Play(); } catch { /* no sound device */ }
        var text = minutes <= 1 ? _texts.LastMinuteBanner : LauncherTexts.Format(_texts.LowTimeBanner, minutes);
        try { new WarningToast(text).Show(); } catch { /* the banner in the window still shows it */ }
    }
}

/// <summary>A short warning on top of all windows (also over a game in borderless mode); it never takes the focus.</summary>
internal sealed class WarningToast : Window
{
    public WarningToast(string text)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(Color.FromRgb(20, 6, 14));
        BorderBrush = new SolidColorBrush(Color.FromRgb(255, 45, 120));
        BorderThickness = new Thickness(2);
        Content = new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(Color.FromRgb(255, 224, 230)), FontFamily = new FontFamily("Consolas"),
            FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(24, 14, 24, 14), MaxWidth = 900, TextWrapping = TextWrapping.Wrap
        };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + 24;
        };
        var close = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        close.Tick += (_, _) => { close.Stop(); Close(); };
        close.Start();
    }
}
