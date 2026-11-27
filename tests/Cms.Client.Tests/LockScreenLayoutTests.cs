using System.Xml.Linq;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>
/// Windows test 2026-11-27, defect 9: the 10-minute banner covered "TIME LEFT", and the UA / EN button fell below the
/// bottom of an 800 px screen. The layout rules are checked in the sources (WPF does not run on Linux).
/// </summary>
public sealed class LockScreenLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void The_low_time_banner_has_its_own_row_in_the_session_panel()
    {
        var doc = XDocument.Load(ThemeTokensParityTests.RepoFile("Cms.Launcher/MainWindow.xaml"));
        var session = Named(doc, "SessionPanel");
        var banner = Named(doc, "WarningBanner");
        Assert.Same(session, banner.Parent);
        Assert.Equal("1", (string?)banner.Attribute("Grid.Row"));
        Assert.Null(banner.Attribute("Margin"));                                           // no overlay over the next row
        var rows = session.Element(Wpf + "Grid.RowDefinitions")!.Elements(Wpf + "RowDefinition").Select(r => (string?)r.Attribute("Height")).ToList();
        Assert.Equal(new[] { "52", "Auto", "Auto", "*", "32" }, rows);
        var countdown = Named(doc, "SessionTimeLabel").Parent!;
        Assert.Equal("2", (string?)countdown.Attribute("Grid.Row"));
    }

    [Fact]
    public void The_lock_overlay_scales_down_to_fit_and_keeps_the_language_button_in_a_corner()
    {
        var code = File.ReadAllText(ThemeTokensParityTests.RepoFile("Cms.Launcher/MainWindow.xaml.cs"));
        Assert.Contains("StretchDirection = StretchDirection.DownOnly", code);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Right,\n            VerticalAlignment = VerticalAlignment.Top", code.Replace("\r\n", "\n"));
        Assert.Contains("var compact = height > 0 && height < 900;", code);
    }
}
