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
        SetResourceReference(BackgroundProperty, "Brush.BgPanel");
        SetResourceReference(BorderBrushProperty, "Brush.Accent");
        SetResourceReference(FontFamilyProperty, "Font.Body");
        BorderThickness = new Thickness(1);

        _pin = new PasswordBox
        {
            Width = 280,
            FontSize = 20,
            Margin = new Thickness(0, 12, 0, 12),
            MaxLength = Cms.Shared.StaffPinHash.MaxLength
        };

        var ok = new Button { Content = texts.Ok, IsDefault = true, Width = 120, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = texts.Cancel, IsCancel = true, Width = 140 };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = texts.PinDialogHeader, FontSize = 16 }.Text("Brush.AccentText", "Font.Display"),
                new TextBlock { Text = texts.PinPrompt, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 280 }.Text("Brush.Text"),
                _pin,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };

        Loaded += (_, _) => { _pin.Focus(); Keyboard.Focus(_pin); };
        // While locked, the dialog takes the foreground at once, also from another program (Windows test 2026-11-27 night,
        // defects 1 and 2); then the PIN box has the keyboard
        ContentRendered += (_, _) =>
        {
            (Owner as MainWindow)?.OnDialogShown(this);
            Keyboard.Focus(_pin);
        };
    }
}
