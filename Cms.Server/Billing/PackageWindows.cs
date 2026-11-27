using Cms.Server.Data;

namespace Cms.Server.Billing;

/// <summary>When a package bought now ends: EndUtc (time runs out), CutoffUtc (hard end of the window).</summary>
public sealed record PackageTerms(DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset? CutoffUtc)
{
    public int Minutes => (int)Math.Round((EndUtc - StartUtc).TotalMinutes);
}

public sealed record PackageAvailability(bool Available, BillingError? Error, PackageTerms? Terms);

/// <summary>
/// Rules of fixed-price packages (club local time):
///  - duration only ("3 години"): ends after the duration; sold on the days of DaysMask;
///  - window ("Нічний 22:00-08:00"): sold only inside the window, which starts on a day of DaysMask;
///    the session ends at the window end (cut-off), wherever it was started; unused time is lost;
///  - window + duration ("Ранковий: 3 год, 08:00-12:00"): ends after the duration or at the cut-off,
///    whichever comes first.
/// Pause and transfer never move a session past its cut-off.
/// </summary>
public static class PackageWindows
{
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMinutes(1);

    public static PackageAvailability Evaluate(PackageEntity package, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (!package.IsActive) return Unavailable(BillingErrors.PackageInactive);
        var hasWindow = package.WindowStartMinute.HasValue && package.WindowEndMinute.HasValue;
        if (!hasWindow && package.DurationMinutes is not > 0) return Unavailable(BillingErrors.PackageInvalid);

        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var day = TariffCalculator.DayIndex(local.DayOfWeek);
        DateTimeOffset? cutoff = null;

        if (hasWindow)
        {
            var start = package.WindowStartMinute!.Value;
            var end = package.WindowEndMinute!.Value;
            var minute = local.Hour * 60 + local.Minute;
            DateTime? windowEndLocal = null;
            if (start < end)
            {
                if (TariffCalculator.HasDay(package.DaysMask, day) && minute >= start && minute < end)
                    windowEndLocal = local.Date.AddMinutes(end);
            }
            else if (TariffCalculator.HasDay(package.DaysMask, day) && minute >= start)
            {
                windowEndLocal = local.Date.AddDays(1).AddMinutes(end);
            }
            else if (TariffCalculator.HasDay(package.DaysMask, day - 1) && minute < end)
            {
                windowEndLocal = local.Date.AddMinutes(end);
            }
            if (windowEndLocal == null)
                return Unavailable(BillingErrors.PackageOutsideWindow.With(
                    TariffCalculator.MinuteText(start), TariffCalculator.MinuteText(end)));
            cutoff = ClubTimeZone.LocalToUtc(windowEndLocal.Value, zone);
        }
        else if (!TariffCalculator.HasDay(package.DaysMask, day))
        {
            return Unavailable(BillingErrors.PackageNotToday);
        }

        var endUtc = package.DurationMinutes is > 0 ? nowUtc.AddMinutes(package.DurationMinutes.Value) : cutoff!.Value;
        if (cutoff.HasValue && cutoff.Value < endUtc) endUtc = cutoff.Value;
        if (endUtc - nowUtc < MinimumLength) return Unavailable(BillingErrors.PackageWindowEnds);
        return new PackageAvailability(true, null, new PackageTerms(nowUtc, endUtc, cutoff));
    }

    /// <summary>Same kind of package (for a transfer to another zone): same duration and window.</summary>
    public static bool SameKind(PackageEntity a, PackageEntity b) =>
        a.DurationMinutes == b.DurationMinutes && a.WindowStartMinute == b.WindowStartMinute && a.WindowEndMinute == b.WindowEndMinute;

    private static PackageAvailability Unavailable(BillingError error) => new(false, error, null);
}
