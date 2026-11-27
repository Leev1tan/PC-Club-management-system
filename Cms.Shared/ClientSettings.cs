using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cms.Shared;

/// <summary>Languages of the lock screen (Cms.Launcher): Ukrainian (default) and English.</summary>
public static class ClientLanguages
{
    public const string Ukrainian = "uk";
    public const string English = "en";
    public static readonly IReadOnlyList<string> All = new[] { Ukrainian, English };

    /// <summary>"uk" or "en" (any case, also "ua"); null for anything else.</summary>
    public static string? Find(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "uk" or "ua" => Ukrainian,
        "en" => English,
        _ => null
    };

    /// <summary>The language, or Ukrainian when it is missing or unknown.</summary>
    public static string Normalize(string? code) => Find(code) ?? Ukrainian;
}

/// <summary>Club currencies that the clients know (the server has the same list in ClubCurrency).</summary>
public static class ClientCurrencies
{
    public const string Default = "UAH";

    private static readonly IReadOnlyDictionary<string, string> Symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["UAH"] = "₴", ["EUR"] = "€", ["USD"] = "$", ["PLN"] = "zł",
    };

    /// <summary>The ISO code in upper case, or UAH when it is missing or unknown.</summary>
    public static string Normalize(string? code) =>
        code != null && Symbols.ContainsKey(code.Trim()) ? code.Trim().ToUpperInvariant() : Default;

    public static string Symbol(string? code) => Symbols[Normalize(code)];
}

/// <summary>
/// Club settings for the PCs. The server sends them with every heartbeat response of an approved PC;
/// the agent writes them to %ProgramData%\ClubAgent\client-settings.json (AgentFiles.ClientSettingsFile),
/// and the lock screen reads that file. Without the file (the server was never reached) the lock screen
/// uses Ukrainian and UAH.
/// </summary>
public sealed class ClientSettings
{
    public static readonly ClientSettings Default = new();

    /// <summary>Lock screen language: uk or en (club setting club.language).</summary>
    public string Language { get; init; } = ClientLanguages.Ukrainian;
    /// <summary>ISO code of the club currency: UAH, EUR, USD or PLN (club setting club.currency).</summary>
    public string Currency { get; init; } = ClientCurrencies.Default;
    /// <summary>When the agent received the settings from the server (null in the server response).</summary>
    public DateTimeOffset? ReceivedUtc { get; init; }

    [JsonIgnore]
    public string CurrencySymbol => ClientCurrencies.Symbol(Currency);

    /// <summary>The same settings with known values only (unknown language: uk, unknown currency: UAH).</summary>
    public ClientSettings Normalized() => new()
    {
        Language = ClientLanguages.Normalize(Language),
        Currency = ClientCurrencies.Normalize(Currency),
        ReceivedUtc = ReceivedUtc
    };

    public bool SameAs(ClientSettings? other) =>
        other != null && Language == other.Language && Currency == other.Currency;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads client-settings.json; null when the text is not valid JSON of this shape.</summary>
    public static ClientSettings? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClientSettings>(json, Json)?.Normalized();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
