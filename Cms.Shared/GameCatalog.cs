using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cms.Shared;

/// <summary>How the lock screen starts a catalog entry.</summary>
public static class GameLaunchKinds
{
    /// <summary>A program: absolute path to an .exe, arguments, working directory.</summary>
    public const string Exe = "exe";
    /// <summary>A Steam game by its AppID (steam://rungameid/&lt;id&gt;).</summary>
    public const string Steam = "steam";
    /// <summary>A launcher link with an allowed scheme, for example com.epicgames.launcher://apps/...</summary>
    public const string Url = "url";

    public static readonly IReadOnlyList<string> All = new[] { Exe, Steam, Url };
}

/// <summary>
/// Rules for a catalog entry, used by the server when staff save it and by the lock screen before it
/// starts it (the lock screen starts only catalog entries that pass these rules).
/// </summary>
public static partial class GameLaunchRules
{
    public const int MaxPath = 260;
    public const int MaxArguments = 500;
    public const int MaxUrl = 500;
    public const int MaxProcessName = 100;

    /// <summary>
    /// URL schemes of game launchers that a catalog entry may use. Web, file and Windows shell schemes
    /// (http, file, ms-settings, search-ms ...) are not allowed: they could open a browser or Explorer.
    /// </summary>
    public static readonly IReadOnlyList<string> UrlSchemes = new[]
    {
        "steam", "com.epicgames.launcher", "uplay", "battlenet", "origin2", "eadesktop", "goggalaxy", "riotclient", "roblox-player"
    };

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|%[A-Za-z][A-Za-z0-9_]{0,40}%\\)[^<>""|?*:\x00-\x1F]+$")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"^[A-Za-z0-9 ._()+-]{1,100}$")]
    private static partial Regex ProcessNamePattern();

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]{0,40}):[^\s\x00-\x1F""<>\\^`{|}]+$")]
    private static partial Regex UrlPattern();

    /// <summary>Absolute Windows path (C:\... or %ProgramFiles%\...) without "..", quotes or wildcards.</summary>
    public static bool IsAbsoluteWindowsPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= MaxPath && WindowsPath().IsMatch(path) &&
        !path.Split('\\').Any(part => part is ".." or ".");

    public static bool IsAllowedUrl(string? url, out string? scheme)
    {
        scheme = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrl) return false;
        var m = UrlPattern().Match(url);
        if (!m.Success) return false;
        scheme = m.Groups["scheme"].Value.ToLowerInvariant();
        return UrlSchemes.Contains(scheme);
    }

    /// <summary>Process name without ".exe" (for example cs2), or null when the value is not a plain file name.</summary>
    public static string? NormalizeProcessName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.Length > 0 && ProcessNamePattern().IsMatch(n) ? n : null;
    }

    /// <summary>Returns an error code (game_bad_...) or null when the entry can be started.</summary>
    public static string? Validate(GameLaunch launch)
    {
        switch (launch.Kind)
        {
            case GameLaunchKinds.Exe:
                if (!IsAbsoluteWindowsPath(launch.ExePath) || !launch.ExePath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return "game_bad_exe";
                if (launch.Arguments is { } args && (args.Length > MaxArguments || args.Any(char.IsControl)))
                    return "game_bad_args";
                if (!string.IsNullOrEmpty(launch.WorkingDirectory) && !IsAbsoluteWindowsPath(launch.WorkingDirectory))
                    return "game_bad_workdir";
                return null;
            case GameLaunchKinds.Steam:
                return launch.SteamAppId is > 0 ? null : "game_bad_steam_id";
            case GameLaunchKinds.Url:
                return IsAllowedUrl(launch.LaunchUrl, out _) ? null : "game_bad_url";
            default:
                return "game_bad_kind";
        }
    }

    /// <summary>The link that starts a Steam game.</summary>
    public static string SteamUrl(int appId) => $"steam://rungameid/{appId}";
}

/// <summary>How to start an entry (kind and the fields of that kind).</summary>
public sealed record GameLaunch(string Kind, string? ExePath = null, string? Arguments = null, string? WorkingDirectory = null,
    int? SteamAppId = null, string? LaunchUrl = null);

/// <summary>The generated tile of a game: initials on a colour (no logos or other art).</summary>
public static class GameTile
{
    /// <summary>Colours for new entries, chosen by the name (the same name gets the same colour).</summary>
    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#e11d48", "#f97316", "#eab308", "#22c55e", "#14b8a6", "#06b6d4",
        "#3b82f6", "#6366f1", "#a855f7", "#d946ef", "#64748b", "#84cc16",
    };

    private static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    public static bool IsColor(string? color) => color != null && ColorPattern.IsMatch(color);

    /// <summary>FNV-1a of the lower-case name picks a palette colour.</summary>
    public static string DefaultColor(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return Palette[(int)(hash % (uint)Palette.Count)];
    }

    /// <summary>
    /// Up to four characters: an acronym as it is (GTA V -> GTA, PUBG -> PUBG, CS2 -> CS2),
    /// else the first letters of the first two words (Counter-Strike 2 -> CS, Apex Legends -> AL), or the
    /// first two letters of a single word (Fortnite -> Fo).
    /// </summary>
    public static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', ':', '_', '.', '/', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0) return "?";
        var first = words[0];
        if (first.Length is >= 2 and <= 4 && first.Any(char.IsLetter) && first.All(c => char.IsUpper(c) || char.IsDigit(c)))
            return first;
        if (words.Count >= 2)
            return (char.ToUpperInvariant(first[0]).ToString() + char.ToUpperInvariant(words[1][0]));
        return first.Length >= 2 ? char.ToUpperInvariant(first[0]) + first.Substring(1, 1).ToLowerInvariant() : first.ToUpperInvariant();
    }
}

/// <summary>
/// One entry of the catalog of a PC: GET /api/devices/{id}/games returns them for the zone of the PC;
/// the agent writes the list to %ProgramData%\ClubAgent\games.json for the lock screen.
/// </summary>
public sealed class CatalogGame
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Kind { get; init; } = GameLaunchKinds.Exe;
    public string? ExePath { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? SteamAppId { get; init; }
    public string? LaunchUrl { get; init; }
    /// <summary>Process that shows the game runs (cs2, FortniteClient-Win64-Shipping); exe entries: the exe name.</summary>
    public string? ProcessName { get; init; }
    public string Color { get; init; } = "#6366f1";
    public string Initials { get; init; } = "?";
    public int SortOrder { get; init; }

    public GameLaunch Launch => new(Kind, ExePath, Arguments, WorkingDirectory, SteamAppId, LaunchUrl);
}

/// <summary>Content of games.json (written by the agent, read by the lock screen).</summary>
public sealed class CatalogFile
{
    public string? ETag { get; init; }
    public DateTimeOffset? ReceivedUtc { get; init; }
    public IReadOnlyList<CatalogGame> Games { get; init; } = Array.Empty<CatalogGame>();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CatalogFile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CatalogFile>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<CatalogGame>? ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<CatalogGame>>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
