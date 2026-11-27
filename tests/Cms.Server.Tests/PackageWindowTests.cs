using Cms.Server.Billing;
using Cms.Server.Data;
using Xunit;
using static Cms.Server.Tests.BillingTestKit;

namespace Cms.Server.Tests;

public sealed class PackageWindowTests
{
    private static PackageEntity Night(int days = All) => new()
    {
        Id = Guid.NewGuid(), Name = "Нічний 22:00–08:00", Price = 250m, WindowStartMinute = 22 * 60, WindowEndMinute = 8 * 60, DaysMask = days
    };

    private static PackageEntity Morning() => new()
    {
        Id = Guid.NewGuid(), Name = "Ранковий", Price = 99m, DurationMinutes = 180, WindowStartMinute = 8 * 60, WindowEndMinute = 12 * 60,
        DaysMask = MonThu | 16
    };

    [Fact]
    public void A_night_pack_ends_at_the_cut_off_wherever_it_starts()
    {
        var evening = PackageWindows.Evaluate(Night(), Local(0, 23), Zone);  // Monday 23:00
        Assert.True(evening.Available);
        Assert.Equal(Local(1, 8), evening.Terms!.EndUtc);                    // Tuesday 08:00
        Assert.Equal(Local(1, 8), evening.Terms.CutoffUtc);
        Assert.Equal(9 * 60, evening.Terms.Minutes);

        var lateNight = PackageWindows.Evaluate(Night(), Local(1, 2, 15), Zone); // Tuesday 02:15 (window of Monday)
        Assert.True(lateNight.Available);
        Assert.Equal(Local(1, 8), lateNight.Terms!.EndUtc);
    }

    [Fact]
    public void A_night_pack_is_not_sold_outside_its_window()
    {
        var noon = PackageWindows.Evaluate(Night(), Local(1, 12), Zone);
        Assert.False(noon.Available);
        Assert.Equal("package_outside_window", noon.Error!.Code);
        Assert.Contains("22:00", noon.Error.Message);

        var lastSeconds = PackageWindows.Evaluate(Night(), Local(1, 7, 59, 30), Zone);
        Assert.False(lastSeconds.Available);
        Assert.Equal("package_window_ends", lastSeconds.Error!.Code);
    }

    [Fact]
    public void The_days_of_a_night_pack_are_the_days_its_window_starts()
    {
        var weekendNights = Night(FriSat);
        Assert.True(PackageWindows.Evaluate(weekendNights, Local(5, 3), Zone).Available);   // Saturday 03:00 (window of Friday)
        Assert.True(PackageWindows.Evaluate(weekendNights, Local(5, 23), Zone).Available);  // Saturday 23:00
        Assert.False(PackageWindows.Evaluate(weekendNights, Local(7, 3), Zone).Available);  // Monday 03:00 (Sunday not in the mask)
        Assert.False(PackageWindows.Evaluate(weekendNights, Local(3, 23), Zone).Available); // Thursday 23:00
    }

    [Fact]
    public void A_morning_pack_ends_after_its_duration_or_at_the_cut_off()
    {
        var early = PackageWindows.Evaluate(Morning(), Local(0, 8, 30), Zone);
        Assert.Equal(Local(0, 11, 30), early.Terms!.EndUtc); // 3 hours
        Assert.Equal(Local(0, 12), early.Terms.CutoffUtc);

        var late = PackageWindows.Evaluate(Morning(), Local(0, 10), Zone);
        Assert.Equal(Local(0, 12), late.Terms!.EndUtc);      // cut-off first
        Assert.Equal(120, late.Terms.Minutes);

        Assert.False(PackageWindows.Evaluate(Morning(), Local(5, 9), Zone).Available); // Saturday
    }

    [Fact]
    public void A_duration_pack_runs_its_hours_and_inactive_packs_are_not_sold()
    {
        var pack = new PackageEntity { Id = Guid.NewGuid(), Name = "3 години", Price = 150m, DurationMinutes = 180 };
        var terms = PackageWindows.Evaluate(pack, Local(2, 21), Zone).Terms!;
        Assert.Equal(Local(3, 0), terms.EndUtc);
        Assert.Null(terms.CutoffUtc);

        pack.IsActive = false;
        Assert.Equal("package_inactive", PackageWindows.Evaluate(pack, Local(2, 21), Zone).Error!.Code);
    }
}
