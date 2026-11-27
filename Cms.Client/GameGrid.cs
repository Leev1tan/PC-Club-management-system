using Cms.Shared;

namespace Cms.Client;

/// <summary>The game grid of the lock screen: search and categories over the catalog of the PC.</summary>
public static class GameGrid
{
    /// <summary>Categories in catalog order (the first entry of each category decides its place).</summary>
    public static IReadOnlyList<string> Categories(IEnumerable<CatalogGame> games) =>
        games.OrderBy(g => g.SortOrder).Select(g => g.Category.Trim()).Where(c => c.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>
    /// Entries of the category (null = all) whose name contains the search text (any case; several
    /// words must all match), in catalog order. Entries that the launch rules refuse are left out.
    /// </summary>
    public static IReadOnlyList<CatalogGame> Filter(IEnumerable<CatalogGame> games, string? category, string? search)
    {
        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return games
            .Where(g => GameLaunchRules.Validate(g.Launch) == null)
            .Where(g => category == null || string.Equals(g.Category.Trim(), category, StringComparison.CurrentCultureIgnoreCase))
            .Where(g => words.All(w => g.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>How the lock screen starts an entry: a program with arguments, or a launcher link through the shell.</summary>
public sealed record GameStartInfo(string FileName, string Arguments, string? WorkingDirectory, bool UseShellExecute);

public static class GameStarter
{
    /// <summary>
    /// The start of a catalog entry, or null when the entry breaks the launch rules (the lock screen
    /// starts only valid catalog entries). %VARIABLES% in paths are expanded by the given function
    /// (Environment.ExpandEnvironmentVariables in the player's session).
    /// </summary>
    public static GameStartInfo? Plan(CatalogGame game, Func<string, string> expand)
    {
        if (GameLaunchRules.Validate(game.Launch) != null) return null;
        switch (game.Kind)
        {
            case GameLaunchKinds.Exe:
                var exe = expand(game.ExePath!);
                var dir = string.IsNullOrEmpty(game.WorkingDirectory) ? null : expand(game.WorkingDirectory);
                // After the expansion the path must still be absolute (a variable could be empty)
                if (!GameLaunchRules.IsAbsoluteWindowsPath(exe) || (dir != null && !GameLaunchRules.IsAbsoluteWindowsPath(dir))) return null;
                dir ??= exe.Contains('\\') ? exe[..exe.LastIndexOf('\\')] : null;
                return new GameStartInfo(exe, game.Arguments ?? string.Empty, dir, UseShellExecute: false);
            case GameLaunchKinds.Steam:
                return new GameStartInfo(GameLaunchRules.SteamUrl(game.SteamAppId!.Value), string.Empty, null, UseShellExecute: true);
            default:
                return new GameStartInfo(game.LaunchUrl!, string.Empty, null, UseShellExecute: true);
        }
    }
}
