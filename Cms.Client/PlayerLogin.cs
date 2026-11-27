using Cms.Shared;

namespace Cms.Client;

/// <summary>What a player can buy on this PC (the answer of GET /api/devices/{id}/login-offers).</summary>
public sealed class LoginOffers
{
    public string Currency { get; init; } = ClientCurrencies.Default;
    public string CurrencySymbol { get; init; } = "₴";
    public TariffOffer? Tariff { get; init; }
    public IReadOnlyList<MinutesOffer> Minutes { get; init; } = Array.Empty<MinutesOffer>();
    public IReadOnlyList<PackageOffer> Packages { get; init; } = Array.Empty<PackageOffer>();
}

public sealed record TariffOffer(Guid Id, string Name, decimal PricePerHourNow, string? RuleNow);
public sealed record MinutesOffer(int Minutes, decimal Price);
public sealed record PackageOffer(Guid Id, string Name, decimal Price, DateTimeOffset EndsAtUtc, int Minutes);

/// <summary>Answer of a successful player login.</summary>
public sealed record LoginResult(Guid SessionId, string Username, string DisplayName, decimal Balance, decimal Bonus, DateTimeOffset? EndUtc, bool FromBalance, string Offer);

/// <summary>Texts of the lock screen for the answers of the agent and the server.</summary>
public static class LoginMessages
{
    public static string For(PipeResponse r, LauncherTexts t)
    {
        var code = r.Code;
        if (r.Status == 401 || code == "login_failed") return t.WrongLogin;
        if (code == "login_locked") return LauncherTexts.Format(t.LoginLocked, r.FirstArg ?? "15");
        return code switch
        {
            "insufficient_balance" or "balance_too_low" => t.InsufficientBalance,
            "player_has_debt" => t.PlayerHasDebt,
            "player_busy" => LauncherTexts.Format(t.PlayerBusy, r.FirstArg ?? "?"),
            "device_busy" => t.DeviceBusy,
            "no_tariff" => t.NoTariff,
            "bad_offer" or "package_not_found" or "package_inactive" or "package_other_zone" or "package_outside_window"
                or "package_not_today" or "package_window_ends" or "package_invalid" => t.PackageUnavailable,
            "agent_unavailable" or "agent_untrusted" or "agent_not_ready" or "agent_error" => t.AgentUnavailable,
            "server_unreachable" => LauncherTexts.Format(t.NoConnection, "—"),
            "session_not_found" or "session_ended" or "session_no_player" => t.SessionGone,
            _ => LauncherTexts.Format(t.ServerError, r.Status)
        };
    }
}

/// <summary>
/// Warnings before the end of the session: at 10, 5 and 1 minute left, each once. When the end moves
/// (an extension, a top-up for a session from the balance), the thresholds that lie behind are not
/// announced again; those ahead are announced when they come.
/// </summary>
public sealed class LowTimeWarnings
{
    public static readonly IReadOnlyList<int> Minutes = new[] { 10, 5, 1 };

    private DateTimeOffset? _end;
    private int _done = int.MaxValue;

    /// <summary>The threshold (minutes) to announce now, or null.</summary>
    public int? Check(DateTimeOffset? endUtc, DateTimeOffset now)
    {
        if (endUtc != _end)
        {
            _end = endUtc;
            _done = int.MaxValue;
            if (endUtc is { } changed)
            {
                var leftNow = changed - now;
                foreach (var m in Minutes.Where(m => leftNow <= TimeSpan.FromMinutes(m))) _done = Math.Min(_done, m);
            }
            return null;
        }
        if (endUtc is not { } end) return null;
        var left = end - now;
        if (left <= TimeSpan.Zero) return null;
        var due = Minutes.Where(m => left <= TimeSpan.FromMinutes(m)).DefaultIfEmpty(int.MaxValue).Min();
        if (due == int.MaxValue || due >= _done) return null;
        _done = due;
        return due;
    }
}
