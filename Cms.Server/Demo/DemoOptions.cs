namespace Cms.Server.Demo;

/// <summary>
/// Demo mode settings.
///   DEMO_MODE=true          (or Demo:Enabled)            - seed a demo club and simulate its PCs
///   DEMO_RESET_HOURS=6      (or Demo:ResetIntervalHours) - wipe and re-seed interval; 0 = only on start
/// </summary>
public sealed class DemoOptions
{
    /// <summary>Player account that exists only in demo mode (documented in README).</summary>
    public const string PlayerLogin = "demo";
    public const string PlayerPassword = "demo";

    public bool Enabled { get; init; }
    public TimeSpan ResetInterval { get; init; } = TimeSpan.FromHours(6);

    public static DemoOptions FromConfiguration(IConfiguration config)
    {
        var enabled = ParseBool(config["DEMO_MODE"]) ?? ParseBool(config["Demo:Enabled"]) ?? false;
        var hours = ParseDouble(config["DEMO_RESET_HOURS"]) ?? ParseDouble(config["Demo:ResetIntervalHours"]) ?? 6;
        return new DemoOptions
        {
            Enabled = enabled,
            ResetInterval = hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.Zero
        };
    }

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "1" or "true" or "yes" or "on" => true,
        _ => false
    };

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : null;
}

/// <summary>Reset times, shown in the admin UI demo banner.</summary>
public sealed class DemoState
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastResetUtc;
    private DateTimeOffset? _nextResetUtc;

    public DateTimeOffset? LastResetUtc { get { lock (_gate) return _lastResetUtc; } }
    public DateTimeOffset? NextResetUtc { get { lock (_gate) return _nextResetUtc; } }

    public void MarkReset(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            _lastResetUtc = now;
            _nextResetUtc = interval > TimeSpan.Zero ? now + interval : null;
        }
    }
}

public static class DemoServiceCollectionExtensions
{
    public static IServiceCollection AddDemoMode(this IServiceCollection services, DemoOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<DemoState>();
        if (!options.Enabled) return services;

        services.AddSingleton<DemoSimulator>();
        services.AddSingleton<DemoResetter>();
        services.AddHostedService<DemoBackgroundService>();
        return services;
    }
}
