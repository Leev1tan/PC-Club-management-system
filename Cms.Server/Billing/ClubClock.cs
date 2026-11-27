namespace Cms.Server.Billing;

/// <summary>
/// Current time and the club time zone. Tariff rules ("Mon-Thu 08:00-17:00") and package windows
/// ("22:00-08:00") are in club local time; the server itself may run in UTC (containers).
///   Club:TimeZone (env Club__TimeZone) - IANA or Windows id, default Europe/Kyiv
/// </summary>
public sealed class ClubClock
{
    public ClubClock(TimeProvider time, TimeZoneInfo zone)
    {
        Time = time;
        Zone = zone;
    }

    public TimeProvider Time { get; }
    public TimeZoneInfo Zone { get; }

    public DateTimeOffset UtcNow => Time.GetUtcNow();

    public DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>Same zone, but "now" is fixed (demo simulator ticks, tests).</summary>
    public ClubClock At(DateTimeOffset utcNow) => new(new FixedTimeProvider(utcNow), Zone);

    public static ClubClock System(TimeZoneInfo? zone = null) => new(TimeProvider.System, zone ?? ClubTimeZone.Default);

    public static ClubClock FromConfiguration(IConfiguration config, ILogger? logger = null)
    {
        var id = config["Club:TimeZone"];
        var zone = ClubTimeZone.Resolve(id, out var usedFallback);
        if (usedFallback)
            logger?.LogWarning("Time zone '{Id}' not found; using {Zone} for tariff rules and packages", id, zone.Id);
        return new ClubClock(TimeProvider.System, zone);
    }
}

public static class ClubTimeZone
{
    public const string DefaultId = "Europe/Kyiv";

    // Europe/Kyiv is new in tzdata 2022b and in Windows ICU; older systems know Europe/Kiev or the Windows id
    private static readonly string[] KyivAliases = { "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time" };

    public static TimeZoneInfo Default { get; } = Resolve(null, out _);

    /// <summary>The requested zone, else Kyiv (any alias), else UTC. usedFallback = the requested id was not found.</summary>
    public static TimeZoneInfo Resolve(string? id, out bool usedFallback)
    {
        usedFallback = false;
        if (!string.IsNullOrWhiteSpace(id))
        {
            var requested = TryFind(id.Trim());
            if (requested != null) return requested;
            usedFallback = true;
        }
        foreach (var alias in KyivAliases)
        {
            var zone = TryFind(alias);
            if (zone != null) return zone;
        }
        usedFallback = true;
        return TimeZoneInfo.Utc;
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    /// <summary>
    /// UTC instant of a club wall-clock time. A time inside the spring-forward gap moves to the end
    /// of the gap; an ambiguous autumn time takes the first (summer) occurrence.
    /// </summary>
    public static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 4 && zone.IsInvalidTime(local); i++)
            local = local.AddMinutes(30);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>A clock that always returns the same instant (settable).</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    public FixedTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}
