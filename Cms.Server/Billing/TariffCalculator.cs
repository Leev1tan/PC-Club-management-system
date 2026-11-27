using Cms.Server.Data;

namespace Cms.Server.Billing;

/// <summary>Time-of-day price of a tariff (club local time); see TariffRuleEntity.</summary>
public sealed record PriceRule(string Name, int DaysMask, int StartMinute, int EndMinute, decimal PricePerHour);

/// <summary>A tariff with its rules, ready for price calculations.</summary>
public sealed record TariffPricing(
    Guid TariffId, string Name, decimal BasePricePerHour, int BillingStepMinutes, decimal MinimumCharge,
    IReadOnlyList<PriceRule> Rules)
{
    /// <summary>Rounding step in minutes (0 in databases of older versions = per minute).</summary>
    public int Step => BillingStepMinutes is > 0 and <= 240 ? BillingStepMinutes : 1;

    public static TariffPricing From(TariffEntity tariff, IEnumerable<TariffRuleEntity> rules) => new(
        tariff.Id, tariff.Name, tariff.PricePerHour, tariff.BillingStepMinutes, tariff.MinimumCharge,
        rules.Where(r => r.TariffId == tariff.Id)
            .OrderBy(r => r.SortOrder)
            .Select(r => new PriceRule(r.Name, r.DaysMask, r.StartMinute, r.EndMinute, r.PricePerHour))
            .ToList());
}

/// <summary>A stretch of time with one price.</summary>
public sealed record PriceSlice(Guid TariffId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, decimal PricePerHour, string? RuleName)
{
    public decimal Amount => PricePerHour * (EndUtc - StartUtc).Ticks / TimeSpan.TicksPerHour;
}

/// <summary>Result of a price calculation; Total is what the player pays.</summary>
public sealed record PriceQuote(
    decimal Total, int PlayedSeconds, int BilledMinutes, bool MinimumApplied, IReadOnlyList<PriceSlice> Slices);

/// <summary>
/// Prices of PC time. A time window is cut into slices at every rule boundary (and at midnight and
/// DST changes, because the price is looked up per club-local minute), and each slice is billed at
/// its own price: a session from 16:30 to 17:30 with a day price until 17:00 and an evening price
/// after it pays 30 minutes of each.
///  - Prepaid time (fixed minutes, extensions): exact minutes, the tariff minimum charge applies.
///  - Post-paid time: the played time of all segments is added up and rounded UP to the tariff
///    step (1 = per minute); the first <see cref="ToleranceSeconds"/> seconds of a started step are
///    free (clock tolerance). The extra rounded time is billed at the price of the last slice.
///    The minimum charge applies when anything was played.
/// Amounts are rounded to 0.01 at the end (half away from zero).
/// </summary>
public static class TariffCalculator
{
    public const int ToleranceSeconds = 10;
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(45);

    /// <summary>ISO day index: Monday = 0 ... Sunday = 6 (bit index in DaysMask).</summary>
    public static int DayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    public static bool HasDay(int mask, int dayIndex) => (mask & (1 << ((dayIndex % 7 + 7) % 7))) != 0;

    /// <summary>True when a window [start, end) (end &lt;= start = past midnight) covers this local time.</summary>
    public static bool WindowCovers(int daysMask, int startMinute, int endMinute, DateTimeOffset local)
    {
        var minute = local.Hour * 60 + local.Minute;
        var day = DayIndex(local.DayOfWeek);
        if (startMinute < endMinute)
            return HasDay(daysMask, day) && minute >= startMinute && minute < endMinute;
        return (HasDay(daysMask, day) && minute >= startMinute) || (HasDay(daysMask, day - 1) && minute < endMinute);
    }

    public static (decimal PricePerHour, string? RuleName) PriceAt(TariffPricing tariff, DateTimeOffset local)
    {
        foreach (var rule in tariff.Rules)
        {
            if (WindowCovers(rule.DaysMask, rule.StartMinute, rule.EndMinute, local))
                return (rule.PricePerHour, rule.Name);
        }
        return (tariff.BasePricePerHour, null);
    }

    /// <summary>Cuts [startUtc, endUtc) into slices of constant price.</summary>
    public static List<PriceSlice> Slice(TariffPricing tariff, DateTimeOffset startUtc, DateTimeOffset endUtc, TimeZoneInfo zone)
    {
        var slices = new List<PriceSlice>();
        if (endUtc <= startUtc) return slices;
        if (endUtc - startUtc > MaxWindow) throw new ArgumentOutOfRangeException(nameof(endUtc), "Time window longer than 45 days.");

        startUtc = startUtc.ToUniversalTime();
        endUtc = endUtc.ToUniversalTime();
        var cursor = startUtc;
        while (cursor < endUtc)
        {
            // Offsets are whole minutes, so a UTC minute is one club-local minute with one price
            var minuteStart = new DateTimeOffset(cursor.Ticks - cursor.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
            var next = minuteStart.AddMinutes(1);
            if (next > endUtc) next = endUtc;
            var (price, rule) = PriceAt(tariff, TimeZoneInfo.ConvertTime(minuteStart, zone));

            var last = slices.Count > 0 ? slices[^1] : null;
            if (last != null && last.PricePerHour == price && last.RuleName == rule)
                slices[^1] = last with { EndUtc = next };
            else
                slices.Add(new PriceSlice(tariff.TariffId, cursor, next, price, rule));
            cursor = next;
        }
        return slices;
    }

    /// <summary>Price of a prepaid window: exact minutes from the start, minimum charge, no rounding.</summary>
    public static PriceQuote QuoteFixed(TariffPricing tariff, DateTimeOffset startUtc, int minutes, TimeZoneInfo zone, bool applyMinimum = true)
    {
        if (minutes <= 0) return new PriceQuote(0m, 0, 0, false, Array.Empty<PriceSlice>());
        var slices = Slice(tariff, startUtc, startUtc.AddMinutes(minutes), zone);
        var total = Round(slices.Sum(s => s.Amount));
        var minimum = applyMinimum && tariff.MinimumCharge > total;
        if (minimum) total = Round(tariff.MinimumCharge);
        return new PriceQuote(total, minutes * 60, minutes, minimum, slices);
    }

    /// <summary>
    /// Price of played (post-paid) time: each segment at its own tariff and rules, the total time
    /// rounded up to the step of <paramref name="billing"/> (the session tariff), minimum charge.
    /// </summary>
    public static PriceQuote QuotePlayed(IEnumerable<(TariffPricing Tariff, DateTimeOffset StartUtc, DateTimeOffset EndUtc)> segments,
        TariffPricing billing, TimeZoneInfo zone)
    {
        var slices = new List<PriceSlice>();
        foreach (var (tariff, start, end) in segments.OrderBy(s => s.StartUtc))
            slices.AddRange(Slice(tariff, start, end, zone));

        var playedTicks = slices.Sum(s => (s.EndUtc - s.StartUtc).Ticks);
        var playedSeconds = (int)(playedTicks / TimeSpan.TicksPerSecond);
        if (playedSeconds <= ToleranceSeconds || slices.Count == 0)
            return new PriceQuote(0m, playedSeconds, 0, false, slices);

        var stepSeconds = billing.Step * 60;
        var steps = (int)Math.Ceiling((playedSeconds - ToleranceSeconds) / (double)stepSeconds);
        var billedMinutes = Math.Max(1, steps) * billing.Step;
        var extraTicks = billedMinutes * TimeSpan.TicksPerMinute - playedTicks;
        var exact = slices.Sum(s => s.Amount) + slices[^1].PricePerHour * extraTicks / TimeSpan.TicksPerHour;

        var total = Round(Math.Max(0m, exact));
        var minimum = billing.MinimumCharge > total;
        if (minimum) total = Round(billing.MinimumCharge);
        return new PriceQuote(total, playedSeconds, billedMinutes, minimum, slices);
    }

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>"HH:mm" of minutes after midnight (1440 = "24:00").</summary>
    public static string MinuteText(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    /// <summary>Parses "HH:mm" (00:00-24:00) to minutes after midnight.</summary>
    public static bool TryParseMinute(string? text, out int minute)
    {
        minute = 0;
        var parts = (text ?? string.Empty).Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h < 0 || m < 0 || m > 59 || h > 24 || (h == 24 && m != 0)) return false;
        minute = h * 60 + m;
        return true;
    }

    /// <summary>ISO weekdays (1 = Monday ... 7 = Sunday) to a DaysMask; null for an empty or invalid list.</summary>
    public static int? DaysToMask(IEnumerable<int>? days)
    {
        if (days == null) return null;
        var mask = 0;
        foreach (var d in days)
        {
            if (d is < 1 or > 7) return null;
            mask |= 1 << (d - 1);
        }
        return mask == 0 ? null : mask;
    }

    public static int[] MaskToDays(int mask) => Enumerable.Range(1, 7).Where(d => (mask & (1 << (d - 1))) != 0).ToArray();
}
