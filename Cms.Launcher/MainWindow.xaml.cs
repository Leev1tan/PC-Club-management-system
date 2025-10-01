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
        var input = Microsoft.VisualBasic.Interaction.InputBox("Enter staff PIN to unlock:", "Staff Unlock", "");
        if (input == "1234")
        {
            try
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var path = System.IO.Path.Combine(programData, "ClubAgent", "state.json");
                var json = "{\"isLocked\":false,\"remainingSeconds\":0}";
                File.WriteAllText(path, json);
                MessageBox.Show("Unlocked by staff.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to unlock: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (!string.IsNullOrWhiteSpace(input))
        {
            MessageBox.Show("Incorrect PIN.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    (_countdown = new TextBlock
                    {
                        Text = string.Empty,
                        Foreground = Brushes.White,
                        FontSize = 28,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0,0,0,8)
                    }),
                    new TextBlock
                    {
                        Text = "This PC is locked",
                        Foreground = Brushes.White,
                        FontSize = 36,
                        HorizontalAlignment = HorizontalAlignment.Center
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
            if (_overlay != null) _overlay.Visibility = isLocked ? Visibility.Visible : Visibility.Collapsed;

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
            var programData2 = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var hb = System.IO.Path.Combine(programData2, "ClubAgent", "agent_heartbeat.txt");
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

            var effectiveLocked = isLocked && !stale;
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