using System.Text.Json;
using Cms.Shared;

namespace Cms.Client;

/// <summary>How the lock screen logs in to the client of one platform: the program and its arguments with {login} and {secret}.</summary>
public sealed class PlatformLogin
{
    /// <summary>Absolute path of the client (for example %ProgramFiles(x86)%\Steam\steam.exe).</summary>
    public string? ClientPath { get; init; }
    /// <summary>Arguments; each space-separated part may hold {login} and {secret} (for example "-login {login} {secret}").</summary>
    public string? LoginArguments { get; init; }
}

/// <summary>
/// account-pool.json (written by the agent from the section AccountPool of its appsettings.json on this PC; only
/// administrators change it, the server never sends it): the platforms that this PC can log in with a club account.
/// </summary>
public sealed class AccountPoolSettings
{
    public IReadOnlyDictionary<string, PlatformLogin> Platforms { get; init; } = new Dictionary<string, PlatformLogin>();

    public static readonly AccountPoolSettings None = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AccountPoolSettings? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<AccountPoolSettings>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The login of a platform (case-insensitive id), or null when this PC cannot log in to it.</summary>
    public PlatformLogin? For(string? platform)
    {
        if (platform == null) return null;
        foreach (var (key, value) in Platforms)
            if (string.Equals(key, platform, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }
}

/// <summary>The program and the argument list that log the client in (the secret is one of the arguments, in memory only).</summary>
public sealed record AccountLoginPlan(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Builds the start of the client of a platform with a club account. The arguments go to the process as an argument
/// list (no shell, no quoting problems). Limit (see docs/AccountPool.md): a program that runs as the player can in
/// principle read the arguments of a process of the player, so the secret is not hidden from such a program.
/// </summary>
public static class AccountLogin
{
    public const string LoginToken = "{login}";
    public const string SecretToken = "{secret}";

    /// <summary>True when the settings can log in: an absolute .exe path and arguments with {login}.</summary>
    public static bool IsUsable(PlatformLogin? p) =>
        p != null && GameLaunchRules.IsAbsoluteWindowsPath(p.ClientPath) && p.ClientPath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(p.LoginArguments) && p.LoginArguments.Contains(LoginToken, StringComparison.Ordinal) &&
        p.LoginArguments.Length <= GameLaunchRules.MaxArguments && !p.LoginArguments.Any(char.IsControl);

    /// <summary>The plan, or null when the settings are not usable or the values are empty.</summary>
    public static AccountLoginPlan? Plan(PlatformLogin? settings, string? login, string? secret, Func<string, string> expand)
    {
        if (!IsUsable(settings) || string.IsNullOrEmpty(login) || secret == null) return null;
        var args = settings!.LoginArguments!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Replace(LoginToken, login, StringComparison.Ordinal).Replace(SecretToken, secret, StringComparison.Ordinal))
            .ToList();
        return new AccountLoginPlan(expand(settings.ClientPath!), args);
    }
}

/// <summary>Names of the platforms on the lock screen, and the texts for the answers to "account".</summary>
public static class AccountMessages
{
    public static string PlatformName(string? platform) => platform?.ToLowerInvariant() switch
    {
        AccountPlatforms.Steam => "Steam",
        AccountPlatforms.Epic => "Epic Games",
        AccountPlatforms.BattleNet => "Battle.net",
        AccountPlatforms.Riot => "Riot Client",
        AccountPlatforms.Ea => "EA app",
        AccountPlatforms.Ubisoft => "Ubisoft Connect",
        AccountPlatforms.Gog => "GOG Galaxy",
        _ => platform ?? "?"
    };

    /// <summary>The text for a refused "account" request (busy pool, no session, the agent refused the client ...).</summary>
    public static string For(PipeResponse r, string platform, LauncherTexts t) => r.Code switch
    {
        "no_free_account" => LauncherTexts.Format(t.AccountBusy, PlatformName(platform)),
        "account_needs_session" or "session_not_found" => t.SessionGone,
        "agent_unavailable" or "agent_untrusted" or "agent_not_ready" or "agent_error" => t.AgentUnavailable,
        "server_unreachable" => LauncherTexts.Format(t.NoConnection, "—"),
        _ => LauncherTexts.Format(t.AccountFailed, r.Code ?? r.Status.ToString(System.Globalization.CultureInfo.InvariantCulture))
    };
}
