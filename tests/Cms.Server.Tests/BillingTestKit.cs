using Cms.Server.Billing;
using Cms.Server.Data;

namespace Cms.Server.Tests;

/// <summary>Club time for billing tests: a fixed UTC+2 zone without DST, and helpers for local times.</summary>
public static class BillingTestKit
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02");

    // 2026-11-23 is a Monday
    public static readonly DateTime Monday = new(2026, 11, 23);

    /// <summary>UTC instant of a club-local time: day offset from Monday 2026-11-23, hour, minute.</summary>
    public static DateTimeOffset Local(int dayFromMonday, int hour, int minute = 0, int second = 0) =>
        new DateTimeOffset(Monday.AddDays(dayFromMonday).AddHours(hour).AddMinutes(minute).AddSeconds(second), TimeSpan.FromHours(2)).ToUniversalTime();

    public static ClubClock Clock(DateTimeOffset utcNow) => new(new FixedTimeProvider(utcNow), Zone);

    public static FixedTimeProvider Time(this ClubClock clock) => (FixedTimeProvider)clock.Time;

    public const int MonThu = 1 | 2 | 4 | 8;
    public const int FriSat = 16 | 32;
    public const int All = 127;

    /// <summary>Base 60 ₴/h; Mon-Thu 08-17: 45; every day 17-22: 70; every night 22-08: 40.</summary>
    public static (TariffEntity Tariff, List<TariffRuleEntity> Rules) Standard(Guid? zoneId = null, int step = 1, decimal minimum = 0m)
    {
        var tariff = new TariffEntity
        {
            Id = Guid.NewGuid(), Name = "Стандарт", PricePerHour = 60m, ZoneId = zoneId, IsDefault = zoneId == null,
            BillingStepMinutes = step, MinimumCharge = minimum
        };
        var rules = new List<TariffRuleEntity>
        {
            Rule(tariff, 0, "Будні вдень", MonThu, 8 * 60, 17 * 60, 45m),
            Rule(tariff, 1, "Вечір", All, 17 * 60, 22 * 60, 70m),
            Rule(tariff, 2, "Ніч", All, 22 * 60, 8 * 60, 40m),
        };
        return (tariff, rules);
    }

    /// <summary>Base 90 ₴/h; every day 17-22: 100; step 5 minutes.</summary>
    public static (TariffEntity Tariff, List<TariffRuleEntity> Rules) Vip(Guid? zoneId = null)
    {
        var tariff = new TariffEntity { Id = Guid.NewGuid(), Name = "VIP", PricePerHour = 90m, ZoneId = zoneId, BillingStepMinutes = 5 };
        return (tariff, new List<TariffRuleEntity> { Rule(tariff, 0, "Вечір VIP", All, 17 * 60, 22 * 60, 100m) });
    }

    public static TariffRuleEntity Rule(TariffEntity tariff, int order, string name, int days, int start, int end, decimal price) => new()
    {
        Id = Guid.NewGuid(), TariffId = tariff.Id, Name = name, DaysMask = days, StartMinute = start, EndMinute = end,
        PricePerHour = price, SortOrder = order
    };

    public static TariffPricing Pricing((TariffEntity Tariff, List<TariffRuleEntity> Rules) t) => TariffPricing.From(t.Tariff, t.Rules);
}
