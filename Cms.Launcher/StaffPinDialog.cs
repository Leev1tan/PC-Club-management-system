using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cms.Client;

namespace Cms.Launcher;

/// <summary>
/// Modal PIN prompt for the staff unlock. The PIN is masked, and the lock screen and the keyboard
/// blocker stay active while the dialog is open (the old InputBox showed the PIN in clear text and
/// removed the lock screen first).
/// </summary>
internal sealed class StaffPinDialog : Window
{
    private readonly PasswordBox _pin;

    public string Pin => _pin.Password;

    public StaffPinDialog(Window owner, LauncherTexts texts)
    {
        Owner = owner;
        Title = texts.PinDialogTitle;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(13, 13, 21));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0, 240, 255));
        BorderThickness = new Thickness(1);

        var font = new FontFamily("Consolas");
        var text = new SolidColorBrush(Color.FromRgb(232, 232, 240));

        _pin = new PasswordBox
        {
            Width = 280,
            FontSize = 20,
            FontFamily = font,
            Margin = new Thickness(0, 12, 0, 12),
            MaxLength = Cms.Shared.StaffPinHash.MaxLength
        };

        var ok = new Button { Content = texts.Ok, IsDefault = true, Width = 120, Margin = new Thickness(0, 0, 8, 0), FontFamily = font };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = texts.Cancel, IsCancel = true, Width = 140, FontFamily = font };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = texts.PinDialogHeader, Foreground = new SolidColorBrush(Color.FromRgb(0, 240, 255)), FontFamily = font, FontSize = 16 },
                new TextBlock { Text = texts.PinPrompt, Foreground = text, FontFamily = font, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 280 },
                _pin,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };

        Loaded += (_, _) => { _pin.Focus(); Keyboard.Focus(_pin); };
    }
}
