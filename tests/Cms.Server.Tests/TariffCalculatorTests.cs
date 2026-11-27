using Cms.Server.Billing;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

public sealed class TariffCalculatorTests
{
    private static readonly TariffPricing Std = Pricing(Standard());

    [Fact]
    public void Test_dates_are_what_the_tests_assume()
    {
        Assert.Equal(DayOfWeek.Monday, Monday.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 14, 30, 0, TimeSpan.Zero), Local(0, 16, 30));
    }

    [Fact]
    public void Prepaid_time_across_a_rule_boundary_is_billed_per_segment()
    {
        // Monday 16:30-17:30: 30 min weekday-day price (45) + 30 min evening price (70)
        var quote = TariffCalculator.QuoteFixed(Std, Local(0, 16, 30), 60, Zone);

        Assert.Equal(57.50m, quote.Total);
        Assert.Collection(quote.Slices,
            s => { Assert.Equal("Будні вдень", s.RuleName); Assert.Equal(Local(0, 17), s.EndUtc); Assert.Equal(22.5m, s.Amount); },
            s => { Assert.Equal("Вечір", s.RuleName); Assert.Equal(35m, s.Amount); });
    }

    [Fact]
    public void Weekend_days_without_a_rule_use_the_base_price()
    {
        // Saturday 10:00-11:00: the weekday rule is Mon-Thu only
        var quote = TariffCalculator.QuoteFixed(Std, Local(5, 10), 60, Zone);
        Assert.Equal(60m, quote.Total);
        Assert.Null(Assert.Single(quote.Slices).RuleName);
    }

    [Fact]
    public void A_night_rule_runs_past_midnight_and_into_the_next_day()
    {
        // Thursday 21:30 - Friday 01:30: 30 min evening (70) + 3.5 h night (40), one night slice across midnight
        var quote = TariffCalculator.QuoteFixed(Std, Local(3, 21, 30), 240, Zone);
        Assert.Equal(35m + 140m, quote.Total);
        Assert.Equal(new[] { "Вечір", "Ніч" }, quote.Slices.Select(s => s.RuleName!).ToArray());
    }

    [Fact]
    public void A_wrapping_rule_belongs_to_the_day_on_which_it_starts()
    {
        // "Weekend nights": starts Friday and Saturday at 22:00, ends 06:00 the next day
        var (tariff, _) = Standard();
        var rules = new[] { Rule(tariff, 0, "Ніч вихідних", FriSat, 22 * 60, 6 * 60, 30m) };
        var pricing = TariffPricing.From(tariff, rules);

        Assert.Equal((30m, "Ніч вихідних"), TariffCalculator.PriceAt(pricing, TimeZoneInfo.ConvertTime(Local(5, 2), Zone)));  // Sat 02:00 (from Fri)
        Assert.Equal((30m, "Ніч вихідних"), TariffCalculator.PriceAt(pricing, TimeZoneInfo.ConvertTime(Local(6, 5, 59), Zone))); // Sun 05:59 (from Sat)
        Assert.Equal((60m, null), TariffCalculator.PriceAt(pricing, TimeZoneInfo.ConvertTime(Local(7, 2), Zone)));  // Mon 02:00 (Sun not in the mask)
        Assert.Equal((60m, null), TariffCalculator.PriceAt(pricing, TimeZoneInfo.ConvertTime(Local(4, 21, 59), Zone))); // Fri 21:59
    }

    [Fact]
    public void Post_paid_time_is_rounded_up_to_the_tariff_step_with_a_small_tolerance()
    {
        var step15 = Pricing(Standard(step: 15));
        var start = Local(5, 10); // Saturday, base price 60 ₴/h

        // 61 minutes -> 75 minutes
        var q1 = TariffCalculator.QuotePlayed(new[] { (step15, start, start.AddMinutes(61)) }, step15, Zone);
        Assert.Equal(75, q1.BilledMinutes);
        Assert.Equal(75m, q1.Total);

        // 60 min 8 s: the first 10 s of a started step are free
        var q2 = TariffCalculator.QuotePlayed(new[] { (Std, start, start.AddMinutes(60).AddSeconds(8)) }, Std, Zone);
        Assert.Equal(60, q2.BilledMinutes);
        Assert.Equal(60m, q2.Total);

        // 60 min 11 s per minute: 61 minutes
        var q3 = TariffCalculator.QuotePlayed(new[] { (Std, start, start.AddMinutes(60).AddSeconds(11)) }, Std, Zone);
        Assert.Equal(61, q3.BilledMinutes);
        Assert.Equal(61m, q3.Total);
    }

    [Fact]
    public void The_minimum_charge_applies_when_anything_was_played()
    {
        var withMinimum = Pricing(Standard(minimum: 20m));
        var start = Local(5, 10);

        var shortSession = TariffCalculator.QuotePlayed(new[] { (withMinimum, start, start.AddMinutes(5)) }, withMinimum, Zone);
        Assert.Equal(20m, shortSession.Total);
        Assert.True(shortSession.MinimumApplied);

        var cancelled = TariffCalculator.QuotePlayed(new[] { (withMinimum, start, start.AddSeconds(5)) }, withMinimum, Zone);
        Assert.Equal(0m, cancelled.Total);

        Assert.Equal(20m, TariffCalculator.QuoteFixed(withMinimum, start, 10, Zone).Total);              // 10 min = 10 ₴ < 20
        Assert.Equal(10m, TariffCalculator.QuoteFixed(withMinimum, start, 10, Zone, applyMinimum: false).Total); // extension
    }

    [Fact]
    public void Segments_of_a_paused_and_transferred_session_are_billed_with_their_own_tariff()
    {
        var vip = Pricing(Vip());
        // Monday 16:00-16:40 on Standard (45), paused 16:40-17:00, 17:00-17:30 on VIP (evening 100)
        var quote = TariffCalculator.QuotePlayed(new[]
        {
            (Std, Local(0, 16), Local(0, 16, 40)),
            (vip, Local(0, 17), Local(0, 17, 30)),
        }, vip, Zone);

        Assert.Equal(70, quote.BilledMinutes); // the pause is not billed; 70 is a multiple of the VIP step (5)
        Assert.Equal(30m + 50m, quote.Total);
    }

    [Fact]
    public void Daylight_saving_change_bills_real_minutes()
    {
        var kyiv = ClubTimeZone.Resolve("Europe/Kyiv", out var fallback);
        if (fallback) return; // no tz database on this machine
        var (tariff, _) = Standard();
        var pricing = TariffPricing.From(tariff, new[] { Rule(tariff, 0, "Ранок", All, 4 * 60, 8 * 60, 30m) });

        // 2026-03-29: 03:00 -> 04:00. 02:30-04:30 local is one real hour: 30 min base (60) + 30 min "Ранок" (30)
        var start = ClubTimeZone.LocalToUtc(new DateTime(2026, 3, 29, 2, 30, 0), kyiv);
        var end = ClubTimeZone.LocalToUtc(new DateTime(2026, 3, 29, 4, 30, 0), kyiv);
        Assert.Equal(TimeSpan.FromHours(1), end - start);
        var quote = TariffCalculator.QuotePlayed(new[] { (pricing, start, end) }, pricing, kyiv);
        Assert.Equal(60, quote.BilledMinutes);
        Assert.Equal(45m, quote.Total);
    }

    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("08:30", 510)]
    [InlineData("24:00", 1440)]
    public void Times_parse_as_minutes_after_midnight(string text, int minutes)
    {
        Assert.True(TariffCalculator.TryParseMinute(text, out var m));
        Assert.Equal(minutes, m);
        Assert.Equal(text, TariffCalculator.MinuteText(m));
    }

    [Theory]
    [InlineData("24:30")]
    [InlineData("7")]
    [InlineData("12:60")]
    [InlineData("")]
    public void Invalid_times_are_refused(string text) => Assert.False(TariffCalculator.TryParseMinute(text, out _));

    [Fact]
    public void Day_lists_convert_to_masks()
    {
        Assert.Equal(MonThu, TariffCalculator.DaysToMask(new[] { 1, 2, 3, 4 }));
        Assert.Equal(new[] { 5, 6 }, TariffCalculator.MaskToDays(FriSat));
        Assert.Null(TariffCalculator.DaysToMask(Array.Empty<int>()));
        Assert.Null(TariffCalculator.DaysToMask(new[] { 0 }));
    }
}
