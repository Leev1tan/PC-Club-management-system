using System.Windows;
using System.Windows.Controls;
using Cms.Client;

namespace Cms.Launcher;

/// <summary>
/// The message and question window of the lock screen, in the club language and the club theme (Windows test 2026-11-27,
/// defect 9: a Windows MessageBox showed "Yes" / "No" in the Windows language under a Ukrainian text). It is owned by the
/// lock screen and top-most, so it stays above the locked screen; Enter is the first button, Esc the last one.
/// </summary>
internal sealed class ClubDialog : Window
{
    private ClubDialog(Window owner, string title, string message, string okText, string? cancelText, bool warning)
    {
        Owner = owner;
        Title = title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.BgPanel");
        SetResourceReference(BorderBrushProperty, warning ? "Brush.Danger" : "Brush.Accent");
        SetResourceReference(FontFamilyProperty, "Font.Body");
        BorderThickness = new Thickness(1);

        var style = owner.TryFindResource("CyberButton") as Style;
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, cancelText == null ? 0 : 10, 0), Style = style };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        if (cancelText != null)
            buttons.Children.Add(new Button { Content = cancelText, IsCancel = true, MinWidth = 110, Style = style });
        else
            ok.IsCancel = true;

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, TextWrapping = TextWrapping.Wrap }.Text(warning ? "Brush.Danger" : "Brush.AccentText", "Font.Display"),
                new TextBlock { Text = message, FontSize = 14, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap }.Text("Brush.Text"),
                buttons
            }
        };
        Loaded += (_, _) => ok.Focus();
        ContentRendered += (_, _) => (Owner as MainWindow)?.OnDialogShown(this); // in front while the PC is locked
    }

    /// <summary>A message with the OK button of the club language.</summary>
    public static void Show(Window owner, LauncherTexts texts, string title, string message, bool warning = false) =>
        new ClubDialog(owner, title, message, texts.Ok, null, warning).ShowDialog();

    /// <summary>A question with Yes / No of the club language; true for Yes.</summary>
    public static bool Ask(Window owner, LauncherTexts texts, string title, string question) =>
        new ClubDialog(owner, title, question, texts.Yes, texts.No, false).ShowDialog() == true;
}
