using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using System.IO;
namespace Cms.Launcher;

public partial class MainWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new();
    private Border? _overlay;
    private TextBlock? _countdown;
    private bool _staffUnlocked = false;  // Local staff override - no file write needed

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+U for staff unlock
        if (e.Key == Key.U && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            e.Handled = true;
            PromptStaffUnlock();
        }
    }

    private void PromptStaffUnlock()
    {
        // Temporarily hide overlay so InputBox is visible
        if (_overlay != null) _overlay.Visibility = Visibility.Collapsed;
        KeyboardBlocker.Disable();
        this.Topmost = false;
        
        var input = Microsoft.VisualBasic.Interaction.InputBox("Enter staff PIN to unlock:", "Staff Unlock", "");
        
        if (input == "1234")
        {
            // Set local override flag - no file write needed!
            _staffUnlocked = true;
            MessageBox.Show("Unlocked by staff.\n\nThis override will remain active until a new lock command is sent from the server.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            // Don't restore overlay - we're unlocked
            return;
        }
        else if (!string.IsNullOrWhiteSpace(input))
        {
            MessageBox.Show("Incorrect PIN.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        
        // Restore overlay on cancel or wrong PIN
        RefreshLockState();
    }


    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var overlayGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        overlayGradient.GradientStops.Add(new GradientStop(Color.FromArgb(240, 10, 14, 23), 0));
        overlayGradient.GradientStops.Add(new GradientStop(Color.FromArgb(245, 15, 22, 41), 0.5));
        overlayGradient.GradientStops.Add(new GradientStop(Color.FromArgb(240, 10, 14, 23), 1));

        _overlay = new Border
        {
            Background = overlayGradient,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "🔒",
                        FontSize = 56,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 16)
                    },
                    new TextBlock
                    {
                        Text = "This PC is locked",
                        Foreground = Brushes.White,
                        FontSize = 32,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        FontFamily = new FontFamily("Segoe UI")
                    },
                    (_countdown = new TextBlock
                    {
                        Text = string.Empty,
                        Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), // #94a3b8
                        FontSize = 22,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 12, 0, 0),
                        FontFamily = new FontFamily("Segoe UI")
                    }),
                    new TextBlock
                    {
                        Text = "Press Ctrl+Shift+U for staff unlock",
                        Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)), // #475569
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 24, 0, 0),
                        FontFamily = new FontFamily("Segoe UI")
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
            grid.Children.Add(_overlay);
            Panel.SetZIndex(_overlay, 9999);
        }

        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, __) => RefreshLockState();
        _timer.Start();
        RefreshLockState();
    }

    private void RefreshLockState()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var path = System.IO.Path.Combine(programData, "ClubAgent", "state.json");
            if (!File.Exists(path))
            {
                if (_overlay != null) _overlay.Visibility = Visibility.Collapsed;
                KeyboardBlocker.Disable();
                return;
            }
            var json = File.ReadAllText(path);
            var isLocked = json.Contains("\"isLocked\":true", StringComparison.OrdinalIgnoreCase);
            
            // If server sent unlock (isLocked=false), clear the staff override
            if (!isLocked)
            {
                _staffUnlocked = false;
            }

            long remaining = 0;
            var remIdx = json.IndexOf("\"remainingSeconds\":", StringComparison.OrdinalIgnoreCase);
            if (remIdx >= 0)
            {
                var after = json.Substring(remIdx + 19);
                var digits = new string(after.TakeWhile(char.IsDigit).ToArray());
                long.TryParse(digits, out remaining);
            }
            if (_countdown != null)
            {
                if (remaining > 0)
                {
                    var ts = TimeSpan.FromSeconds(remaining);
                    _countdown.Text = $"Time remaining: {ts:hh\\:mm\\:ss}";
                    _countdown.Visibility = Visibility.Visible;
                }
                else
                {
                    _countdown.Text = string.Empty;
                    _countdown.Visibility = Visibility.Collapsed;
                }
            }

            // Fail-open if agent heartbeat stale (>15s) to avoid permanent lock if service is down
            var hb = System.IO.Path.Combine(programData, "ClubAgent", "agent_heartbeat.txt");
            var stale = false;
            try
            {
                if (File.Exists(hb))
                {
                    var text = File.ReadAllText(hb);
                    if (DateTimeOffset.TryParse(text, out var dt) && DateTimeOffset.UtcNow - dt > TimeSpan.FromSeconds(15))
                    {
                        stale = true;
                    }
                }
                else
                {
                    stale = true;
                }
            }
            catch { }

            // Staff unlock overrides everything
            var effectiveLocked = isLocked && !stale && !_staffUnlocked;
            if (_overlay != null) _overlay.Visibility = effectiveLocked ? Visibility.Visible : Visibility.Collapsed;
            this.Topmost = effectiveLocked;
            if (effectiveLocked) KeyboardBlocker.Enable(); else KeyboardBlocker.Disable();

            // Update main UI countdown as well
            try
            {
                var label = this.FindName("MainCountdown") as TextBlock;
                if (label != null)
                {
                    if (remaining > 0)
                    {
                        var ts = TimeSpan.FromSeconds(remaining);
                        label.Text = $"Time remaining: {ts:hh\\:mm\\:ss}";
                    }
                    else
                    {
                        label.Text = "Time remaining: 00:00:00";
                    }
                }
            }
            catch { }
        }
        catch
        {
            // ignore
        }
    }
}