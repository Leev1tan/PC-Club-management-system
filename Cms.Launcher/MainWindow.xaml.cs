using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cms.Client;
using Cms.Shared;

namespace Cms.Launcher;

public partial class MainWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new();
    private Border? _lockOverlay;
    private TextBlock? _lockCountdown;
    private TextBlock? _lockReasonText;
    private TextBlock? _lockReservedText;
    private TextBlock? _lockTitle;
    private TextBlock? _lockHint;
    private TextBlock? _lockStaffHint;
    private TextBlock? _lockIcon;
    private Button? _languageButton;

    // Club settings from the agent (client-settings.json): language and currency; Ukrainian without the file.
    // The player may switch the language on the lock screen until the PC locks again.
    private ClientSettings _settings = ClientSettings.Default;
    private string? _languageOverride;
    private LauncherTexts _texts = LauncherTexts.Uk;
    private bool _wasLocked = true;
    private LockDecision? _lastDecision;

    // Fail-closed lock decision and staff PIN (Cms.Shared, unit-tested)
    private readonly LockScreenController _lock = new();
    private readonly PinAttemptLimiter _pinLimiter = new();
    private bool _pinPromptOpen;
    private LauncherState? _lastState;
    private DateTimeOffset _lastStateReadUtc = DateTimeOffset.MinValue;

    // "Log off" of the staff: the agent forwards it in state.json, the lock screen logs this Windows session off
    private readonly LogoffTrigger _logoff = new(DateTimeOffset.UtcNow);
    private bool _loggingOff;

    // Kiosk mode
    private bool _kioskMode = false;
    private KioskKeyboardHook? _kioskHook;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnWindowClosing;
        if (Application.Current != null) Application.Current.SessionEnding += (_, _) => ReportSessionEnd();
        KeyDown += OnKeyDown;

        // Allow Enter in login form
        UsernameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) PasswordBox.Focus(); };
        PasswordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnLoginClick(null!, null!); };

        // Detect kiosk mode
        DetectKioskMode();
    }

    private void DetectKioskMode()
    {
        try
        {
            // Check for kiosk.json next to the executable
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var kioskConfigPath = Path.Combine(exeDir, "kiosk.json");
            if (File.Exists(kioskConfigPath))
            {
                _kioskMode = true;
                _kioskHook = new KioskKeyboardHook();
                _kioskHook.Install();
            }
        }
        catch { /* not in kiosk mode */ }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_kioskMode && !_lock.StaffOverride && !_loggingOff)
        {
            // Prevent closing in kiosk mode unless staff unlocked
            e.Cancel = true;
        }
        else
        {
            // Cleanup
            _kioskHook?.Dispose();
        }
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
    /// <summary>
    /// Emergency unlock with the staff PIN. The PIN hash comes from the server (set by the owner in
    /// the admin UI) through the agent (staff-pin.json) and is verified here, so it also works while
    /// the server or the agent is down. The lock screen and the keyboard blocker stay active.
    /// </summary>
    private async void PromptStaffUnlock()
    {
        if (_pinPromptOpen) return;

        var wait = _pinLimiter.WaitTime(DateTimeOffset.UtcNow);
        if (wait is { } w)
        {
            ClubDialog.Show(this, _texts, _texts.UnlockTitle, LauncherTexts.Format(_texts.PinWait, Math.Ceiling(w.TotalSeconds)), warning: true);
            return;
        }

        var hash = ReadStaffPinHash();
        if (hash == null)
        {
            ClubDialog.Show(this, _texts, _texts.UnlockTitle, _texts.PinNotSet);
            return;
        }

        _pinPromptOpen = true;
        try
        {
            var dialog = new StaffPinDialog(this, _texts);
            if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.Pin)) return;

            var pin = dialog.Pin;
            var ok = await Task.Run(() => StaffPinHash.Verify(pin, hash)); // PBKDF2 takes a moment
            if (ok)
            {
                _pinLimiter.RecordSuccess();
                ReportStaffUnlock();
                _lock.BeginStaffOverride(Snapshot(_lastState));
                ClubDialog.Show(this, _texts, _texts.Done, _texts.PinUnlocked);
            }
            else
            {
                _pinLimiter.RecordFailure(DateTimeOffset.UtcNow);
                ClubDialog.Show(this, _texts, _texts.Error, _texts.PinWrong, warning: true);
            }
        }
        finally
        {
            _pinPromptOpen = false;
            RefreshLockState();
        }
    }

    /// <summary>
    /// Leaves a note of the emergency unlock in the agent inbox; the agent sends it to the server's
    /// audit log (also later, when the agent or the server was down). Best effort: never blocks the unlock.
    /// </summary>
    private void ReportStaffUnlock()
    {
        try
        {
            var reason = _lock.Evaluate(Snapshot(_lastState), ReadAgentHeartbeat(), DateTimeOffset.UtcNow).Reason;
            WriteInboxEvent(new LauncherEvent { LockReason = reason.ToString(), WindowsUser = Environment.UserName });
        }
        catch
        {
            // no inbox (older install) or no access: the unlock still works
        }
    }

    /// <summary>The PIN hash from staff-pin.json, or null (no PIN, unreadable, or not written by the agent).</summary>
    private string? ReadStaffPinHash()
    {
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.StaffPinFile);
            if (!File.Exists(path) || !IsTrustedAgentFile(path)) return null;
            var file = JsonSerializer.Deserialize<StaffPinFile>(ReadShared(path), LauncherJson.Options);
            return StaffPinHash.IsWellFormed(file?.Hash) ? file!.Hash : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// In kiosk mode, ignore agent files that the signed-in (player) account owns: the agent writes
    /// them as a service account, so a file owned by the player was planted to fake the lock state or PIN.
    /// </summary>
    private bool IsTrustedAgentFile(string path)
    {
        if (!_kioskMode) return true;
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
            var self = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            return owner != null && self != null && !owner.Equals(self);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads a file that the agent may replace at the same moment.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ──── EXTEND SESSION ────
    private void OnExtendClick(object sender, RoutedEventArgs e)
    {
        ClubDialog.Show(this, _texts, _texts.ExtendTitle, _texts.ExtendHint);
    }

    // ──── LANGUAGE ────

    /// <summary>
    /// Reads client-settings.json (written by the agent from the club settings). A missing or untrusted
    /// file means Ukrainian and UAH. The texts change at once when the club changes the setting.
    /// </summary>
    private void RefreshClientSettings()
    {
        ClientSettings settings;
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.ClientSettingsFile);
            settings = File.Exists(path) && IsTrustedAgentFile(path) ? ClientSettings.Parse(ReadShared(path)) ?? ClientSettings.Default : ClientSettings.Default;
        }
        catch
        {
            settings = ClientSettings.Default;
        }
        ApplyTheme(settings); // theme, accent and logo follow at once (cheap when nothing changed)
        if (settings.SameAs(_settings) && _texts.Language == LauncherTexts.For(_languageOverride ?? settings.Language).Language) return;
        _settings = settings;
        ApplyTexts();
    }

    /// <summary>UA / EN on the lock screen: the player's choice lasts until the PC locks again.</summary>
    private void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        _languageOverride = _texts.Other.Language;
        ApplyTexts();
    }

    /// <summary>Puts the texts of the current language on every element of the window.</summary>
    private void ApplyTexts()
    {
        _texts = LauncherTexts.For(_languageOverride ?? _settings.Language);
        Title = _texts.WindowTitle;
        LoginRequiredText.Text = _texts.LoginRequired;
        UsernameLabel.Text = _texts.UsernameLabel;
        SecretLabel.Text = _texts.SecretLabel;
        if (LoginButton.IsEnabled) LoginButton.Content = _texts.LoginButton;
        OffersTitleText.Text = _texts.OffersTitle;
        EndSessionButton.Content = _texts.EndSessionButton;
        ApplyOrderTexts();
        RenderOffers();
        SessionActiveText.Text = _texts.SessionActive;
        SessionStaffHint.Text = _texts.StaffHint;
        ExtendButton.Content = _texts.ExtendButton;
        if (_lockTitle != null) _lockTitle.Text = _texts.LockedTitle;
        if (_lockHint != null) _lockHint.Text = _texts.LockedHint;
        if (_lockStaffHint != null) _lockStaffHint.Text = _texts.StaffHint;
        if (_languageButton != null) _languageButton.Content = _texts.LanguageSwitch;
        UpdateSessionHeader();
        RenderGames();
        RefreshGameNow();
        if (_lastDecision != null) UpdateLockReason(_lastDecision);
        UpdateCountdowns(_lastState?.RemainingSeconds ?? 0);
    }

    // ──── INITIALIZATION ────
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The lock overlay, in the colours and fonts of the club theme (resource keys of Themes/*.xaml)
        var lockIcon = _lockIcon = new TextBlock
        {
            Text = "⛔",
            FontSize = 56,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Glow(lockIcon, "Color.Danger", 20);

        var lockTitle = _lockTitle = new TextBlock
        {
            Text = _texts.LockedTitle,
            FontSize = 42,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        }.Text("Brush.AccentText", "Font.Display");
        Glow(lockTitle, "Color.Accent", 24);

        _lockCountdown = new TextBlock
        {
            Text = string.Empty,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0)
        }.Text("Brush.Danger", "Font.Mono");

        _lockReservedText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed
        }.Text("Brush.Warn");

        _lockReasonText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed
        }.Text("Brush.Danger");

        // The lock screen fits 1024 x 768, 1280 x 800 and larger (Windows test 2026-11-27, defect 9): the stack scales down
        // only when it does not fit (Viewbox, DownOnly; MaxWidth so long texts wrap), a compact layout below 900 px height,
        // and the UA / EN button in the top right corner, where it is always visible.
        var lockStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 900,
            Children =
            {
                lockIcon,
                lockTitle,
                _lockCountdown,
                _lockReservedText,
                _lockReasonText,
                (_lockHint = new TextBlock
                {
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 0)
                }.Text("Brush.TextMuted")),
                // Staff hint: hardly visible on purpose
                (_lockStaffHint = new TextBlock
                {
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                }.Text("Brush.Border", "Font.Mono"))
            }
        };
        _languageButton = new Button
        {
            Style = (Style)FindResource("CyberButton"),
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 24, 0)
        };
        _lockOverlay = new Border
        {
            BorderThickness = new Thickness(0),
            Child = new Grid
            {
                Children =
                {
                    new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16, 12, 16, 12), Child = lockStack },
                    _languageButton
                }
            },
            Visibility = Visibility.Visible, // locked until the first state read says otherwise
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = true
        };
        _lockOverlay.SetResourceReference(Border.BackgroundProperty, "Brush.Overlay");

        if (Content is Grid grid)
        {
            grid.Children.Add(_lockOverlay);
            Panel.SetZIndex(_lockOverlay, 9999);
        }
        _languageButton.Click += OnLanguageClick;

        // The login form is part of the lock screen: a locked PC without a session asks for the player login
        if (Content is Grid main && main.Children.Contains(LoginPanel))
        {
            main.Children.Remove(LoginPanel);
            lockStack.Children.Insert(lockStack.Children.IndexOf(_lockStaffHint!), LoginPanel);
        }
        _lockOverlay.SizeChanged += (_, e) => ApplyLockLayout(e.NewSize.Height);
        ApplyLockLayout(SystemParameters.PrimaryScreenHeight);
        try { SessionHostname.Text = Environment.MachineName; } catch { SessionHostname.Text = string.Empty; }

        RefreshClientSettings();
        ApplyTexts();

        // Poll every 2 seconds
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, __) => RefreshLockState();
        _timer.Start();
        RefreshLockState();
        StartLockGuard(); // while locked: on top and in the foreground every second, foreign top-most windows pushed down
    }

    /// <summary>Below 900 px height (1024 x 768, 1280 x 800): no big icon, a smaller title and logo, so less scaling is needed.</summary>
    private void ApplyLockLayout(double height)
    {
        var compact = height > 0 && height < 900;
        if (_lockIcon != null) _lockIcon.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (_lockTitle != null) _lockTitle.FontSize = compact ? 32 : 42;
        LoginLogo.MaxHeight = compact ? 64 : 96;
    }

    // ──── LOCK STATE POLLING ────
    /// <summary>
    /// Fails CLOSED: the PC stays locked when the agent heartbeat is older than 15 s, when
    /// state.json is missing or unreadable, or on any error. Only the staff PIN unlocks it then.
    /// </summary>
    private void RefreshLockState()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var state = ReadAgentState(now);
            var heartbeat = ReadAgentHeartbeat();
            var decision = _lock.Evaluate(Snapshot(state), heartbeat, now);
            _lastDecision = decision;
            ReportStatus(decision, state?.LockGeneration);
            EnsureWatchdog();

            // A new lock ends the player's language choice; the teardown runs when the session is over
            if (decision.Locked && !_wasLocked) _languageOverride = null;
            _wasLocked = decision.Locked;
            CheckTeardown(decision, state);
            if (_logoff.ShouldLogOff(state?.Logoff, now)) LogOffWindowsSession();
            RefreshClientSettings();
            RefreshCatalog();
            TrackGames();
            RefreshGameNow();

            UpdateSessionHeader();
            UpdateCountdowns(state?.RemainingSeconds ?? 0);
            UpdateLockReason(decision);
            UpdateLoginPanel(decision);

            if (decision.Locked)
                SetLocked();
            else
                SetUnlocked();
        }
        catch
        {
            if (!_lock.StaffOverride) SetLocked(); // fail closed on errors
        }
    }

    /// <summary>The lock state for the decision: the lock generation makes every lock command a new lock event.</summary>
    private static AgentLockSnapshot? Snapshot(LauncherState? state) =>
        state == null ? null : new AgentLockSnapshot(state.IsLocked, state.LockChangedUtc, state.LockGeneration);

    /// <summary>state.json; a read error keeps the last good state for up to 15 s, then null.</summary>
    private LauncherState? ReadAgentState(DateTimeOffset now)
    {
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.StateFile);
            if (File.Exists(path) && IsTrustedAgentFile(path))
            {
                var state = JsonSerializer.Deserialize<LauncherState>(ReadShared(path), LauncherJson.Options);
                if (state != null)
                {
                    _lastState = state;
                    _lastStateReadUtc = now;
                    return state;
                }
            }
            else
            {
                _lastState = null;
                return null;
            }
        }
        catch
        {
            // the agent replaces the file at this moment; use the last state for a short time
        }
        return now - _lastStateReadUtc <= AgentFiles.AgentSilentAfter ? _lastState : null;
    }

    private DateTimeOffset? ReadAgentHeartbeat()
    {
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.HeartbeatFile);
            if (!File.Exists(path) || !IsTrustedAgentFile(path)) return null;
            return DateTimeOffset.TryParse(ReadShared(path).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var hb) ? hb : null;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateLockReason(LockDecision decision)
    {
        if (_lockReasonText == null) return;
        var text = decision.Reason switch
        {
            LockReason.AgentSilent => _texts.AgentNotResponding,
            LockReason.NoAgentState => _texts.NoAgentState,
            LockReason.LockedByAgent when _lastState?.Session is { Paused: true } => _texts.SessionPaused,
            _ => string.Empty
        };
        _lockReasonText.Text = text;
        _lockReasonText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_lockReservedText != null)
        {
            // "Reserved for <nickname or first name> at HH:MM" while the server holds the PC for a reservation;
            // "Event «name» at HH:MM", then the time left of the event, while an event books the PC
            var reserved = ReservationNotice.LockLine(_lastState?.Reservation, _texts, DateTimeOffset.UtcNow);
            _lockReservedText.Text = reserved ?? string.Empty;
            _lockReservedText.Visibility = reserved != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateCountdowns(long remaining)
    {
        // Lock overlay countdown
        if (_lockCountdown != null)
        {
            if (remaining > 0)
            {
                _lockCountdown.Text = LauncherTexts.Format(_texts.LockedTimeLeft, LauncherFormat.Clock(remaining));
                _lockCountdown.Visibility = Visibility.Visible;
            }
            else
            {
                _lockCountdown.Text = string.Empty;
                _lockCountdown.Visibility = Visibility.Collapsed;
            }
        }

        // Session panel countdown and the warnings at 10, 5 and 1 minute left
        var unlocked = _lastDecision is { Locked: false };
        var session = _lastState?.Session;
        if (remaining > 0)
        {
            SessionCountdown.Text = LauncherFormat.Clock(remaining);
            SessionTimeLabel.Text = session?.FromBalance == true ? _texts.BalanceTimeLabel : _texts.TimeLeftLabel;
            var low = unlocked && remaining <= 600;
            WarningBanner.Visibility = low ? Visibility.Visible : Visibility.Collapsed;
            WarningText.Text = remaining <= 60 ? _texts.LastMinuteBanner : LauncherTexts.Format(_texts.LowTimeBanner, (int)Math.Ceiling(remaining / 60.0));
        }
        else
        {
            SessionCountdown.Text = "∞";
            SessionTimeLabel.Text = _texts.OpenEndedLabel;
            WarningBanner.Visibility = Visibility.Collapsed;
        }
        if (unlocked) AnnounceLowTime();
    }

    private void SetLocked()
    {
        if (_lockOverlay != null) _lockOverlay.Visibility = Visibility.Visible;
        SessionPanel.Visibility = Visibility.Collapsed; // nothing behind the lock screen takes keyboard focus
        this.Topmost = true;
        KeyboardBlocker.Enable();
    }

    private void SetUnlocked()
    {
        if (_lockOverlay != null) _lockOverlay.Visibility = Visibility.Collapsed;
        SessionPanel.Visibility = Visibility.Visible;
        this.Topmost = false;
        KeyboardBlocker.Disable();
    }
}

// ──── DTOs ────
internal static class LauncherJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal record LauncherConfig(string? ServerUrl);

internal record LauncherState(
    bool IsLocked,
    long RemainingSeconds,
    DateTimeOffset? SessionEndUtc,
    string? LockReason,
    string? ServerUrl,
    DateTimeOffset? LockChangedUtc = null,
    string? Enrollment = null)
{
    /// <summary>Process id of the agent (the pipe server must be this process).</summary>
    public int AgentPid { get; init; }
    /// <summary>The number of lock commands of the agent (0 for an older agent): a new value ends a staff PIN unlock.</summary>
    public long LockGeneration { get; init; }
    /// <summary>Active session from the server (player, tariff, planned end), for display.</summary>
    public ClientSessionInfo? Session { get; init; }
    /// <summary>"Log off" of the staff, forwarded by the agent.</summary>
    public LogoffRequest? Logoff { get; init; }
    /// <summary>The reservation that holds the PC now.</summary>
    public ClientReservationInfo? Reservation { get; init; }
}
