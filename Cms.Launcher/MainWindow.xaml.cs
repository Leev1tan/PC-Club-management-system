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

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
            Child = new TextBlock
            {
                Text = "This PC is locked",
                Foreground = Brushes.White,
                FontSize = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
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
            if (isLocked) KeyboardBlocker.Enable(); else KeyboardBlocker.Disable();
        }
        catch
        {
            // ignore
        }
    }
}