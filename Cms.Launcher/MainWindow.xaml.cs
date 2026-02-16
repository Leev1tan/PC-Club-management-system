using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cms.Launcher;

public partial class MainWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new();
    private readonly HttpClient _http = new();
    private Border? _lockOverlay;
    private TextBlock? _lockCountdown;
    private bool _staffUnlocked = false;

    // Session state
    private Guid? _userId;
    private string? _username;
    private decimal _balance;
    private bool _isGuest;


    // Server URL from agent state
    private string _serverUrl = "http://localhost:5081";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;

        // Allow Enter in login form
        UsernameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) PasswordBox.Focus(); };
        PasswordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnLoginClick(null!, null!); };
    }

    // ──── KEYBOARD SHORTCUTS ────
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.U &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            e.Handled = true;
            PromptStaffUnlock();
        }
    }

    // ──── STAFF UNLOCK ────
    private void PromptStaffUnlock()
    {
        if (_lockOverlay != null) _lockOverlay.Visibility = Visibility.Collapsed;
        KeyboardBlocker.Disable();
        this.Topmost = false;

        var input = Microsoft.VisualBasic.Interaction.InputBox("Введіть PIN персоналу:", "Розблокування", "");

        if (input == "1234")
        {
            _staffUnlocked = true;
            MessageBox.Show("Розблоковано персоналом.\n\nЦе перевизначення діятиме до нової команди блокування від сервера.",
                "Успіх", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        else if (!string.IsNullOrWhiteSpace(input))
        {
            MessageBox.Show("Невірний PIN.", "Помилка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshLockState();
    }

    // ──── LOGIN ────
    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowLoginError("Введіть ім'я користувача");
            return;
        }

        LoginButton.IsEnabled = false;
        LoginButton.Content = "Вхід...";
        LoginError.Visibility = Visibility.Collapsed;

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverUrl}/api/users/login",
                new { Username = username, Password = string.IsNullOrEmpty(password) ? null : password });

            if (resp.IsSuccessStatusCode)
            {
                var user = await resp.Content.ReadFromJsonAsync<UserResponse>();
                if (user != null)
                {
                    _userId = user.id;
                    _username = user.username;
                    _balance = user.balance;
                    _isGuest = false;
                    EnterSessionMode();
                    return;
                }
            }

            var status = (int)resp.StatusCode;
            ShowLoginError(status == 401 ? "Невірний логін або пароль" : $"Помилка сервера ({status})");
        }
        catch (Exception ex)
        {
            ShowLoginError($"Немає зв'язку з сервером: {ex.Message}");
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "Увійти";
        }
    }

    private void OnGuestClick(object sender, RoutedEventArgs e)
    {
        _userId = null;
        _username = "Гість";
        _balance = 0;
        _isGuest = true;
        EnterSessionMode();
    }

    private void ShowLoginError(string msg)
    {
        LoginError.Text = msg;
        LoginError.Visibility = Visibility.Visible;
    }

    // ──── SESSION MODE ────
    private void EnterSessionMode()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = Visibility.Visible;

        SessionUser.Text = _isGuest ? "👤 Гість" : $"👤 {_username}";
        SessionBalance.Text = _isGuest ? "" : $"💰 {_balance:N0} ₴";

        try
        {
            SessionHostname.Text = Environment.MachineName;
        }
        catch { SessionHostname.Text = ""; }
    }

    // ──── EXTEND SESSION ────
    private void OnExtendClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Зверніться до адміністратора або поповніть баланс для подовження сесії.",
            "Подовження сесії", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ──── CALL STAFF ────
    private void OnCallStaffClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Виклик надіслано. Адміністратор скоро підійде.",
            "Виклик персоналу", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ──── INITIALIZATION ────
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Build cyberpunk lock overlay
        var overlayBrush = new SolidColorBrush(Color.FromArgb(248, 5, 5, 8)); // Near-black

        // Neon cyan for text glow
        var neonCyan = new SolidColorBrush(Color.FromRgb(0, 240, 255));
        var neonMagenta = new SolidColorBrush(Color.FromRgb(255, 45, 120));
        var mutedText = new SolidColorBrush(Color.FromRgb(85, 85, 112));
        var monoFont = new FontFamily("Consolas");

        // Lock icon with glow
        var lockIcon = new TextBlock
        {
            Text = "⛔",
            FontSize = 56,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        lockIcon.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(255, 45, 120), BlurRadius = 20, ShadowDepth = 0, Opacity = 0.6
        };

        // Main title with neon glow
        var lockTitle = new TextBlock
        {
            Text = "[ LOCKED ]",
            Foreground = neonCyan,
            FontSize = 42,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = monoFont
        };
        lockTitle.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(0, 240, 255), BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6
        };

        _lockCountdown = new TextBlock
        {
            Text = string.Empty,
            Foreground = neonMagenta,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            FontFamily = monoFont
        };

        _lockOverlay = new Border
        {
            Background = overlayBrush,
            BorderBrush = new SolidColorBrush(Color.FromRgb(34, 34, 53)),
            BorderThickness = new Thickness(0),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    lockIcon,
                    lockTitle,
                    _lockCountdown,
                    new TextBlock
                    {
                        Text = "> АВТОРИЗУЙТЕСЬ АБО ЗВЕРНІТЬСЯ ДО ПЕРСОНАЛУ",
                        Foreground = mutedText,
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 24, 0, 0),
                        FontFamily = monoFont
                    },
                    new TextBlock
                    {
                        Text = "CTRL+SHIFT+U :: STAFF_OVERRIDE",
                        Foreground = new SolidColorBrush(Color.FromRgb(26, 26, 48)),
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 0),
                        FontFamily = monoFont
                    }
                }
            },
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = true
        };

        if (Content is Grid grid)
        {
            grid.Children.Add(_lockOverlay);
            Panel.SetZIndex(_lockOverlay, 9999);
        }

        // Read server URL from agent config
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var configPath = Path.Combine(programData, "ClubAgent", "config.json");
            if (File.Exists(configPath))
            {
                var configJson = File.ReadAllText(configPath);
                var urlIdx = configJson.IndexOf("\"serverUrl\":", StringComparison.OrdinalIgnoreCase);
                if (urlIdx >= 0)
                {
                    var afterQuote = configJson.IndexOf('"', urlIdx + 12) + 1;
                    var endQuote = configJson.IndexOf('"', afterQuote);
                    if (afterQuote > 0 && endQuote > afterQuote)
                        _serverUrl = configJson.Substring(afterQuote, endQuote - afterQuote);
                }
            }
        }
        catch { }

        // Poll every 2 seconds
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, __) => RefreshLockState();
        _timer.Start();
        RefreshLockState();
    }

    // ──── LOCK STATE POLLING ────
    private void RefreshLockState()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var path = Path.Combine(programData, "ClubAgent", "state.json");

            if (!File.Exists(path))
            {
                SetUnlocked();
                return;
            }

            var json = File.ReadAllText(path);
            var isLocked = json.Contains("\"isLocked\":true", StringComparison.OrdinalIgnoreCase);

            // If server sent unlock, clear staff override
            if (!isLocked)
                _staffUnlocked = false;

            // Parse remaining seconds
            long remaining = 0;
            var remIdx = json.IndexOf("\"remainingSeconds\":", StringComparison.OrdinalIgnoreCase);
            if (remIdx >= 0)
            {
                var after = json.Substring(remIdx + 19);
                var digits = new string(after.TakeWhile(char.IsDigit).ToArray());
                long.TryParse(digits, out remaining);
            }

            // Update lock overlay countdown
            if (_lockCountdown != null)
            {
                if (remaining > 0)
                {
                    var ts = TimeSpan.FromSeconds(remaining);
                    _lockCountdown.Text = $"Залишилось: {ts:hh\\:mm\\:ss}";
                    _lockCountdown.Visibility = Visibility.Visible;
                }
                else
                {
                    _lockCountdown.Text = string.Empty;
                    _lockCountdown.Visibility = Visibility.Collapsed;
                }
            }

            // Update session panel countdown
            if (SessionPanel.Visibility == Visibility.Visible)
            {
                if (remaining > 0)
                {
                    var ts = TimeSpan.FromSeconds(remaining);
                    SessionCountdown.Text = $"{ts:hh\\:mm\\:ss}";
                    SessionTimeLabel.Text = "Залишилось часу";

                    // 5-minute warning
                    if (remaining <= 300 && remaining > 0)
                    {
                        WarningBanner.Visibility = Visibility.Visible;
                        var mins = (int)Math.Ceiling(remaining / 60.0);
                        WarningText.Text = $"⚠️  Залишилось {mins} хв! Поповніть баланс або подовжіть сесію";
                    }
                    else
                    {
                        WarningBanner.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    SessionCountdown.Text = "∞";
                    SessionTimeLabel.Text = "Безлімітна сесія";
                    WarningBanner.Visibility = Visibility.Collapsed;
                }
            }

            // Parse tariff info
            try
            {
                var tariffIdx = json.IndexOf("\"tariffName\":", StringComparison.OrdinalIgnoreCase);
                if (tariffIdx >= 0 && SessionPanel.Visibility == Visibility.Visible)
                {
                    var aq = json.IndexOf('"', tariffIdx + 13) + 1;
                    var eq = json.IndexOf('"', aq);
                    if (aq > 0 && eq > aq)
                        SessionTariff.Text = $"Тариф: {json.Substring(aq, eq - aq)}";
                }

                var costIdx = json.IndexOf("\"currentCost\":", StringComparison.OrdinalIgnoreCase);
                if (costIdx >= 0 && SessionPanel.Visibility == Visibility.Visible)
                {
                    var after = json.Substring(costIdx + 14);
                    var numStr = new string(after.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                    if (decimal.TryParse(numStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var cost))
                    {
                        SessionCost.Text = $"Вартість: {cost:N0} ₴";
                    }
                }
            }
            catch { }

            // Fail-open: if agent heartbeat is stale (>15s)
            var hb = Path.Combine(programData, "ClubAgent", "agent_heartbeat.txt");
            var stale = false;
            try
            {
                if (File.Exists(hb))
                {
                    var text = File.ReadAllText(hb);
                    if (DateTimeOffset.TryParse(text, out var dt) && DateTimeOffset.UtcNow - dt > TimeSpan.FromSeconds(15))
                        stale = true;
                }
                else
                {
                    stale = true;
                }
            }
            catch { }

            // Effective lock state
            var effectiveLocked = isLocked && !stale && !_staffUnlocked;
            if (effectiveLocked)
                SetLocked();
            else
                SetUnlocked();
        }
        catch
        {
            // Fail open on errors
        }
    }

    private void SetLocked()
    {
        if (_lockOverlay != null) _lockOverlay.Visibility = Visibility.Visible;
        this.Topmost = true;
        KeyboardBlocker.Enable();
    }

    private void SetUnlocked()
    {
        if (_lockOverlay != null) _lockOverlay.Visibility = Visibility.Collapsed;
        this.Topmost = false;
        KeyboardBlocker.Disable();
    }
}

// ──── DTOs ────
internal record UserResponse(Guid id, string username, string displayName, decimal balance, decimal bonusPoints);