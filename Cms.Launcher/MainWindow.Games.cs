using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cms.Client;
using Cms.Shared;

namespace Cms.Launcher;

/// <summary>
/// Game grid of the session screen: the catalog of this PC's zone from games.json (written by the agent),
/// search and categories (Cms.Client.GameGrid), generated tiles (initials on a colour), and the start of
/// an entry. Only catalog entries that pass Cms.Shared.GameLaunchRules start (Cms.Client.GameStarter).
/// Started games are followed (Cms.Client.GameTracker) and reported to the agent inbox.
/// </summary>
public partial class MainWindow
{
    private readonly IProcessTable _processes = new SystemProcessTable();
    private readonly GameTracker _gameTracker = new();
    private IReadOnlyList<CatalogGame> _catalog = Array.Empty<CatalogGame>();
    private DateTime _catalogWriteUtc;
    private string? _gameCategory;
    private int _gameTicks;

    /// <summary>Reads games.json when the agent wrote a new one (a planted or unreadable file counts as no catalog).</summary>
    private void RefreshCatalog()
    {
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.CatalogFile);
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (written == _catalogWriteUtc) return;
            _catalogWriteUtc = written;
            _catalog = written != DateTime.MinValue && IsTrustedAgentFile(path)
                ? CatalogFile.Parse(ReadShared(path))?.Games ?? Array.Empty<CatalogGame>()
                : Array.Empty<CatalogGame>();
        }
        catch
        {
            _catalog = Array.Empty<CatalogGame>();
        }
        if (_gameCategory != null && !GameGrid.Categories(_catalog).Contains(_gameCategory)) _gameCategory = null;
        RenderGames();
    }

    private void RenderGames()
    {
        GamesTitleText.Text = _texts.GamesTitle;
        GameSearchHint.Text = _texts.GamesSearch;

        GameCategories.Children.Clear();
        var categories = GameGrid.Categories(_catalog);
        if (categories.Count > 1)
        {
            GameCategories.Children.Add(CategoryChip(_texts.GamesAll, null));
            foreach (var c in categories) GameCategories.Children.Add(CategoryChip(c, c));
        }
        RenderTiles();
    }

    private Button CategoryChip(string text, string? category)
    {
        var chip = new Button
        {
            Content = text,
            Style = (Style)FindResource("CyberButton"),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Opacity = _gameCategory == category ? 1.0 : 0.55
        };
        chip.Click += (_, _) => { _gameCategory = category; RenderGames(); };
        return chip;
    }

    private void RenderTiles()
    {
        GameTiles.Children.Clear();
        var games = GameGrid.Filter(_catalog, _gameCategory, GameSearchBox.Text);
        foreach (var game in games) GameTiles.Children.Add(Tile(game));
        GamesEmptyText.Text = _catalog.Count == 0 ? _texts.GamesEmpty : _texts.GamesNoMatch;
        GamesEmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGameSearchChanged(object sender, TextChangedEventArgs e) => RenderTiles();

    /// <summary>A generated tile: initials on the entry's colour, the name below. No logos or other art.</summary>
    private Button Tile(CatalogGame game)
    {
        var color = GameTile.IsColor(game.Color) ? (Color)ColorConverter.ConvertFromString(game.Color) : Color.FromRgb(99, 102, 241);
        var tile = new Button
        {
            Style = (Style)FindResource("CyberButton"),
            Width = 150,
            Height = 160,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 12, 12),
            ToolTip = game.Name,
            Content = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Width = 92, Height = 92, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(color),
                        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10),
                        Child = new TextBlock
                        {
                            Text = game.Initials, Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                        }
                    },
                    new TextBlock
                    {
                        Text = game.Name, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                        MaxWidth = 130, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
        tile.Click += (_, _) => StartGame(game);
        return tile;
    }

    private bool _accountBusy;

    /// <summary>account-pool.json of the agent (a planted or unreadable file counts as no platform).</summary>
    private AccountPoolSettings ReadAccountPoolSettings()
    {
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.AccountPoolFile);
            if (File.Exists(path) && IsTrustedAgentFile(path)) return AccountPoolSettings.Parse(ReadShared(path)) ?? AccountPoolSettings.None;
        }
        catch
        {
            // unreadable: no platform
        }
        return AccountPoolSettings.None;
    }

    /// <summary>
    /// A game that needs a club account: the agent leases one for this session and, the first time, hands over the
    /// credentials; the client of the platform starts with the login arguments of this PC. The secret is never shown;
    /// it lives only in this method and in the argument list of the client process. Next time (the same session) the
    /// client is logged in already and the game starts at once.
    /// </summary>
    private async Task<bool> PrepareAccountAsync(CatalogGame game)
    {
        var platform = game.AccountPlatform!;
        var name = AccountMessages.PlatformName(platform);
        var login = ReadAccountPoolSettings().For(platform);
        if (!AccountLogin.IsUsable(login))
        {
            MessageBox.Show(this, LauncherTexts.Format(_texts.AccountNotConfigured, name), _texts.AccountTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var res = await AskAgentAsync(new PipeRequest(PipeOps.Account, GameId: game.Id));
        if (!res.Ok)
        {
            MessageBox.Show(this, AccountMessages.For(res, platform, _texts), _texts.AccountTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var lease = res.As<AccountLease>();
        if (lease == null)
        {
            MessageBox.Show(this, LauncherTexts.Format(_texts.AccountFailed, "—"), _texts.AccountTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (lease.Delivered) return true;
        var plan = AccountLogin.Plan(login, lease.Login, lease.Secret, Environment.ExpandEnvironmentVariables);
        lease = lease with { Login = null, Secret = null };
        if (plan == null)
        {
            MessageBox.Show(this, LauncherTexts.Format(_texts.AccountFailed, "—"), _texts.AccountTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        GameNowText.Text = LauncherTexts.Format(_texts.AccountLoggingIn, lease.LoginMask, name);
        try
        {
            var psi = new ProcessStartInfo(plan.FileName) { UseShellExecute = false };
            foreach (var argument in plan.Arguments) psi.ArgumentList.Add(argument);
            using var client = Process.Start(psi);
            if (client != null)
            {
                try { _startedProcesses.Add(new StartedProcess(client.Id, client.ProcessName)); } catch (InvalidOperationException) { /* exited at once */ }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(this, LauncherTexts.Format(_texts.AccountFailed, ex.GetType().Name), _texts.AccountTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        // The client logs in before the game asks it to start
        await Task.Delay(TimeSpan.FromSeconds(3));
        return true;
    }

    /// <summary>Starts a catalog entry (only while the PC is unlocked) and reports it to the agent.</summary>
    private async void StartGame(CatalogGame game)
    {
        if (_lastDecision is not { Locked: false }) return;
        if (game.AccountPlatform != null)
        {
            if (_accountBusy) return;
            _accountBusy = true;
            try
            {
                if (!await PrepareAccountAsync(game)) return;
            }
            finally
            {
                _accountBusy = false;
            }
            if (_lastDecision is not { Locked: false }) return;
        }
        var plan = GameStarter.Plan(game, Environment.ExpandEnvironmentVariables);
        if (plan == null)
        {
            MessageBox.Show(this, LauncherTexts.Format(_texts.GameStartFailed, game.Name, "—"), _texts.GameStartTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(plan.FileName, plan.Arguments) { UseShellExecute = plan.UseShellExecute };
            if (plan.WorkingDirectory != null && Directory.Exists(plan.WorkingDirectory)) psi.WorkingDirectory = plan.WorkingDirectory;
            using var process = Process.Start(psi);
            int? pid = null;
            string? processName = null;
            if (!plan.UseShellExecute && process != null)
            {
                try { pid = process.Id; processName = process.ProcessName; } catch (InvalidOperationException) { /* exited at once */ }
            }
            RememberStarted(game, pid, processName);
            ReportGameEvents(new[] { _gameTracker.Started(game, pid, DateTimeOffset.UtcNow) });
            GameNowText.Text = LauncherTexts.Format(_texts.GameNow, game.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, LauncherTexts.Format(_texts.GameStartFailed, game.Name, ex.Message), _texts.GameStartTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Every 5 s: which started games still run (stops and a re-reported game go to the agent).</summary>
    private void TrackGames()
    {
        if (++_gameTicks % 3 != 0) return;
        try
        {
            ReportGameEvents(_gameTracker.Update(_processes.Snapshot(), _processes.CurrentSessionId, DateTimeOffset.UtcNow));
            var running = _gameTracker.Running;
            if (running.Count == 0) GameNowText.Text = string.Empty;
        }
        catch
        {
            // the process list is not readable at this moment; next time
        }
    }

    private void ReportGameEvents(IEnumerable<GameEvent> events)
    {
        foreach (var e in events)
            WriteInboxEvent(new LauncherEvent { Type = e.Type, GameId = e.GameId, OccurredUtc = e.AtUtc, WindowsUser = Environment.UserName });
    }

    /// <summary>
    /// Drops an event into the agent inbox (players may only create files there). Best effort: the lock
    /// screen keeps working when the inbox is missing (older install) or not writable.
    /// </summary>
    private static void WriteInboxEvent(LauncherEvent e)
    {
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.InboxDirectoryName, LauncherEvent.FileName(e.Id));
            File.WriteAllText(path, JsonSerializer.Serialize(e));
        }
        catch
        {
            // no inbox or no access
        }
    }
}
