using System.Text.RegularExpressions;
using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

public sealed class LauncherTextsTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}");

    [Fact]
    public void Every_text_exists_in_both_languages_with_the_same_placeholders()
    {
        var uk = LauncherTexts.Uk.All();
        var en = LauncherTexts.En.All();
        Assert.Equal(uk.Keys.OrderBy(k => k), en.Keys.OrderBy(k => k));
        Assert.True(uk.Count > 30, "the lock screen has more texts than this");
        foreach (var (name, text) in uk)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"uk.{name} is empty");
            Assert.False(string.IsNullOrWhiteSpace(en[name]), $"en.{name} is empty");
            var ukArgs = Placeholder.Matches(text).Select(m => m.Value).Distinct().OrderBy(x => x);
            var enArgs = Placeholder.Matches(en[name]).Select(m => m.Value).Distinct().OrderBy(x => x);
            Assert.True(ukArgs.SequenceEqual(enArgs), $"{name}: placeholders differ ({text} / {en[name]})");
        }
    }

    [Fact]
    public void English_texts_have_no_Ukrainian_letters_and_Ukrainian_texts_are_Ukrainian()
    {
        var cyrillic = new Regex("[Ѐ-ӿ]");
        foreach (var (name, text) in LauncherTexts.En.All())
            Assert.False(cyrillic.IsMatch(text), $"en.{name} has Cyrillic letters: {text}");
        // Texts that are the same in both languages are only symbols and technical names
        var same = LauncherTexts.Uk.All().Where(kv => kv.Value == LauncherTexts.En.All()[kv.Key]).Select(kv => kv.Key).ToList();
        Assert.Equal(new[] { "Ok" }, same);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("uk", "uk")]
    [InlineData("ua", "uk")]
    [InlineData(null, "uk")]
    [InlineData("de", "uk")]
    public void The_language_code_selects_the_texts_with_Ukrainian_as_the_fallback(string? code, string expected)
    {
        Assert.Equal(expected, LauncherTexts.For(code).Language);
        Assert.NotEqual(expected, LauncherTexts.For(code).Other.Language);
    }

    [Fact]
    public void Placeholders_are_filled_with_the_invariant_culture()
    {
        Assert.Equal("Залишилось: 01:05:09", LauncherTexts.Format(LauncherTexts.Uk.LockedTimeLeft, LauncherFormat.Clock(3909)));
        Assert.Equal("Server error (500)", LauncherTexts.Format(LauncherTexts.En.ServerError, 500));
    }

    [Theory]
    [InlineData("UAH", 150, "150.00 ₴")]
    [InlineData("EUR", 12.5, "12.50 €")]
    [InlineData("USD", 0.999, "1.00 $")]
    [InlineData("PLN", 7, "7.00 zł")]
    [InlineData("GBP", 7, "7.00 ₴")]
    public void Money_has_two_decimals_and_the_club_currency(string currency, double amount, string expected)
    {
        var settings = new ClientSettings { Currency = currency }.Normalized();
        Assert.Equal(expected, LauncherFormat.Money((decimal)amount, settings));
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(-5, "00:00:00")]
    [InlineData(59, "00:00:59")]
    [InlineData(36000 + 61, "10:01:01")]
    [InlineData(100 * 3600, "100:00:00")]
    public void Clock_shows_hours_minutes_seconds(long seconds, string expected) =>
        Assert.Equal(expected, LauncherFormat.Clock(seconds));
}

public sealed class ClientSettingsTests
{
    [Fact]
    public void Missing_or_unknown_values_fall_back_to_Ukrainian_and_UAH()
    {
        Assert.Null(ClientSettings.Parse(null));
        Assert.Null(ClientSettings.Parse("not json"));
        Assert.Null(ClientSettings.Parse("[1,2]"));
        var parsed = ClientSettings.Parse("""{"language":"fr","currency":"XYZ"}""")!;
        Assert.Equal(("uk", "UAH", "₴"), (parsed.Language, parsed.Currency, parsed.CurrencySymbol));
        Assert.Equal(("uk", "UAH"), (ClientSettings.Default.Language, ClientSettings.Default.Currency));
    }

    [Fact]
    public void The_agent_file_round_trips()
    {
        var settings = new ClientSettings { Language = "en", Currency = "pln", ReceivedUtc = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero) }.Normalized();
        var back = ClientSettings.Parse(settings.ToJson())!;
        Assert.Equal(("en", "PLN", "zł"), (back.Language, back.Currency, back.CurrencySymbol));
        Assert.Equal(settings.ReceivedUtc, back.ReceivedUtc);
        Assert.True(back.SameAs(settings));
        Assert.False(back.SameAs(new ClientSettings { Language = "en", Currency = "EUR" }));
        Assert.DoesNotContain("currencySymbol", settings.ToJson());
    }
}
