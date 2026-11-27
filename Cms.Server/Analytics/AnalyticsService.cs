using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Shifts;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Analytics;

/// <summary>A period of club days: [FromUtc, EndUtc), EndUtc = the end of the last day or now, whichever is first.</summary>
public sealed record AnalyticsPeriod(string Key, DateOnly From, DateOnly To, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset EndUtc)
{
    public const int MaxDays = 366;

    /// <summary>today, 7d (today and the 6 days before), 30d, or the club dates from..to (inclusive). Null: bad input.</summary>
    public static AnalyticsPeriod? Resolve(string? period, DateOnly? from, DateOnly? to, ClubClock clock)
    {
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(clock.ToLocal(now).DateTime);
        string key;
        if (from != null || to != null)
        {
            if (from == null || to == null || to < from || to.Value.DayNumber - from.Value.DayNumber >= MaxDays || from > today) return null;
            key = "custom";
        }
        else
        {
            key = (period ?? "7d").Trim().ToLowerInvariant();
            var days = key switch { "today" => 1, "7d" => 7, "30d" => 30, _ => 0 };
            if (days == 0) return null;
            from = today.AddDays(1 - days);
            to = today;
        }
        var fromUtc = ClubTimeZone.LocalToUtc(from.Value.ToDateTime(TimeOnly.MinValue), clock.Zone);
        var toUtc = ClubTimeZone.LocalToUtc(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), clock.Zone);
        return new AnalyticsPeriod(key, from.Value, to.Value, fromUtc, toUtc, toUtc < now ? toUtc : now);
    }

    public bool Contains(DateTimeOffset utc) => utc >= FromUtc && utc < EndUtc;
}

public sealed record AnalyticsRevenue(decimal Total, decimal Time, decimal Bar, MethodSums ByMethod);
public sealed record AnalyticsRefunds(decimal Bar, int BarReceipts, decimal Balance, int BalanceRefunds)
{
    public decimal Total => Bar + Balance;
}
/// <summary>Average value of one paid session (PC time) and of one bar receipt; null when there is none.</summary>
public sealed record AnalyticsAverage(decimal? Session, int Sessions, decimal? Receipt, int Receipts);
/// <summary>One bar of the chart: a club day or, for a one-day period, a club hour.</summary>
public sealed record AnalyticsPoint(DateTimeOffset StartUtc, string Label, decimal Time, decimal Bar, decimal Total, MethodSums ByMethod, decimal? Occupancy);
public sealed record ZoneOccupancy(Guid? ZoneId, string Name, string Color, int Pcs, decimal BusyHours, decimal CapacityHours, decimal? Percent);
public sealed record AnalyticsOccupancy(decimal? Percent, int Pcs, decimal BusyHours, decimal CapacityHours, IReadOnlyList<ZoneOccupancy> Zones);
/// <summary>Occupancy % per weekday (0 = Monday ... 6 = Sunday) and club hour (0 ... 23); null = the hour is not in the period.</summary>
public sealed record AnalyticsHeatmap(IReadOnlyList<IReadOnlyList<decimal?>> Percent, decimal? Max);
public sealed record ShiftVariance(long Id, DateTimeOffset ClosedUtc, string? ClosedBy, decimal Variance, string? Note);
public sealed record AnalyticsShifts(int Closed, decimal Variance, decimal Shortage, decimal Surplus, IReadOnlyList<ShiftVariance> WithVariance);
public sealed record AnalyticsSums(decimal Time, decimal Bar, decimal CashNet, decimal CardNet, int Rows);

/// <summary>
/// Reconciliation: LedgerRows of the period; Reconciled = every desk amount is in one section and the
/// payment methods add up to the revenue; ZReports = the Z-report sums of the shifts that lie fully in
/// the period (all their ledger rows, as the Z-report counts them) and OutsideShifts = the other rows of
/// the period; ShiftsAddUp = the two together give the totals of the period.
/// </summary>
public sealed record AnalyticsCheck(int LedgerRows, long? FirstId, long? LastId, bool Reconciled, int ShiftsInside,
    AnalyticsSums ZReports, AnalyticsSums OutsideShifts, bool ShiftsAddUp);

/// <summary>
/// Owner analytics of a period (spec area 10). Money comes from the ledger rows of the period, added up
/// by <see cref="LedgerTotals"/> like the Z-report: revenue = PC time (sessions and packages, whatever
/// paid for them: cash, card, balance, bonus, debt) + bar (sales - refunds). Top-ups are deposits, not
/// revenue. Occupancy comes from the session segments (play time per PC; a pause is not play time):
/// busy PC-hours / (approved PCs x hours of the period up to now), per zone and per weekday x club hour.
/// </summary>
public sealed record AnalyticsReport(
    string Period, DateOnly From, DateOnly To, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset EndUtc,
    string TimeZone, CurrencyView Currency, DateTimeOffset GeneratedUtc,
    AnalyticsRevenue Revenue, ZTime Time, ZBar Bar, ZCount TopUps, AnalyticsRefunds Refunds, ZBonus Bonus, ZDebt Debt,
    decimal Payouts, AnalyticsAverage AverageTicket, ZDesk Cash, ZDesk Card,
    string Granularity, IReadOnlyList<AnalyticsPoint> Series,
    AnalyticsOccupancy Occupancy, AnalyticsHeatmap Heatmap, AnalyticsShifts Shifts, AnalyticsCheck Check);

public static class AnalyticsService
{
    private const int TopItems = 10;

    /// <summary>Ledger rows with CreatedUtc in [fromUtc, toUtc), oldest first (SQLite cannot compare DateTimeOffset in SQL).</summary>
    public static async Task<List<LedgerEntryEntity>> LedgerRowsAsync(CmsDbContext db, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var query = db.Ledger.AsNoTracking().OrderBy(l => l.Id).AsQueryable();
        if (db.Database.IsNpgsql()) return await query.Where(l => l.CreatedUtc >= fromUtc && l.CreatedUtc < toUtc).ToListAsync(ct);
        var rows = new List<LedgerEntryEntity>();
        await foreach (var l in query.AsAsyncEnumerable().WithCancellation(ct))
            if (l.CreatedUtc >= fromUtc && l.CreatedUtc < toUtc) rows.Add(l);
        return rows;
    }

    public static async Task<AnalyticsReport> BuildAsync(CmsDbContext db, AnalyticsPeriod period, ClubClock clock, CancellationToken ct = default)
    {
        var zone = clock.Zone;
        var rows = await LedgerRowsAsync(db, period.FromUtc, period.EndUtc, ct);
        var total = LedgerTotals.Of(rows);
        var hourly = period.From == period.To;

        // Chart buckets: club days, or club hours of a one-day period
        var slots = Slots(period, zone, hourly);
        var buckets = slots.Select(_ => new LedgerTotals()).ToArray();
        foreach (var row in rows)
        {
            var i = SlotIndex(slots, row.CreatedUtc);
            if (i >= 0) buckets[i].Add(row);
        }

        var occupancy = await OccupancyAsync(db, period, zone, slots, clock.UtcNow, ct);
        var series = slots.Select((s, i) => new AnalyticsPoint(
            s.StartUtc, s.Label, buckets[i].TimeTotal, buckets[i].BarNet, buckets[i].RevenueTotal, buckets[i].Revenue,
            Percent(occupancy.SlotBusy[i], occupancy.SlotCapacity[i]))).ToList();

        var items = await TopItemsAsync(db, rows, ct);
        var shifts = await ShiftsAsync(db, period, ct);
        var check = await CheckAsync(db, period, rows, total, clock.UtcNow, ct);

        return new AnalyticsReport(
            period.Key, period.From, period.To, period.FromUtc, period.ToUtc, period.EndUtc,
            zone.Id, CurrencyView.From(await db.CurrencyAsync(ct)), clock.UtcNow,
            new AnalyticsRevenue(total.RevenueTotal, total.TimeTotal, total.BarNet, total.Revenue),
            new ZTime(total.TimeTotal, total.Time, total.TimeRows),
            new ZBar(total.BarSales, total.BarRefunds, total.BarNet, total.Receipts, total.RefundReceipts, items),
            new ZCount(total.TopUps, total.TopUpCount),
            new AnalyticsRefunds(total.BarRefunds.Total, total.RefundReceipts, total.Refunds.Total, total.RefundCount),
            new ZBonus(total.BonusIssued, total.BonusSpent, total.BonusReversed, total.BonusAdjusted),
            new ZDebt(total.DebtRecorded, total.DebtPaid, total.DebtWrittenOff),
            total.Payouts,
            new AnalyticsAverage(
                total.Sessions > 0 ? TariffCalculator.Round(total.TimeTotal / total.Sessions) : null, total.Sessions,
                total.Receipts > 0 ? TariffCalculator.Round(total.BarSales.Total / total.Receipts) : null, total.Receipts),
            new ZDesk(total.CashIn, total.CashOut, total.CashNet), new ZDesk(total.CardIn, total.CardOut, total.CardNet),
            hourly ? "hour" : "day", series,
            occupancy.Summary, occupancy.Heatmap, shifts, check);
    }

    /// <summary>Revenue (PC time + bar net) of today, the last 7 and the last 30 club days (dashboard tiles).</summary>
    public static async Task<(decimal Today, decimal Week, decimal Month)> RevenueAsync(CmsDbContext db, ClubClock clock, CancellationToken ct = default)
    {
        var month = AnalyticsPeriod.Resolve("30d", null, null, clock)!;
        var week = AnalyticsPeriod.Resolve("7d", null, null, clock)!;
        var today = AnalyticsPeriod.Resolve("today", null, null, clock)!;
        var rows = await LedgerRowsAsync(db, month.FromUtc, month.EndUtc, ct);
        decimal Sum(AnalyticsPeriod p) => LedgerTotals.Of(rows.Where(r => r.CreatedUtc >= p.FromUtc)).RevenueTotal;
        return (Sum(today), Sum(week), Sum(month));
    }

    private static decimal? Percent(double busy, double capacity) =>
        capacity <= 0 ? null : Math.Round((decimal)(busy / capacity * 100), 1);

    // ===== Time slots in club time =====

    public sealed record Slot(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Label);

    private static List<Slot> Slots(AnalyticsPeriod period, TimeZoneInfo zone, bool hourly)
    {
        var list = new List<Slot>();
        if (hourly)
        {
            var day = period.From.ToDateTime(TimeOnly.MinValue);
            for (var h = 0; h < 24; h++)
            {
                var start = ClubTimeZone.LocalToUtc(day.AddHours(h), zone);
                var end = ClubTimeZone.LocalToUtc(day.AddHours(h + 1), zone);
                if (start >= period.EndUtc) break;
                if (end > start) list.Add(new Slot(start, end, $"{h:00}:00"));
            }
            return list;
        }
        for (var d = period.From; d <= period.To; d = d.AddDays(1))
        {
            var start = ClubTimeZone.LocalToUtc(d.ToDateTime(TimeOnly.MinValue), zone);
            if (start >= period.EndUtc) break;
            list.Add(new Slot(start, ClubTimeZone.LocalToUtc(d.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), d.ToString("yyyy-MM-dd")));
        }
        return list;
    }

    private static int SlotIndex(List<Slot> slots, DateTimeOffset utc)
    {
        for (var i = slots.Count - 1; i >= 0; i--)
            if (utc >= slots[i].StartUtc) return utc < slots[i].EndUtc ? i : -1;
        return -1;
    }

    /// <summary>
    /// Splits [from, to) at the club hour boundaries and calls add(piece start, local weekday 0-6, local hour, minutes).
    /// DST: the skipped hour has no piece; the repeated hour gets two hours.
    /// </summary>
    private static void ByClubHour(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, Action<DateTimeOffset, int, int, double> add)
    {
        var t = from;
        while (t < to)
        {
            var local = TimeZoneInfo.ConvertTime(t, zone).DateTime;
            var hourStart = local.Date.AddHours(local.Hour);
            var next = ClubTimeZone.LocalToUtc(hourStart.AddHours(1), zone);
            if (next <= t) next = t.AddMinutes(60 - local.Minute).AddSeconds(-local.Second); // guard against odd zones
            var end = next < to ? next : to;
            add(t, ((int)local.DayOfWeek + 6) % 7, local.Hour, (end - t).TotalMinutes);
            t = end;
        }
    }

    // ===== Occupancy =====

    private sealed record OccupancyResult(AnalyticsOccupancy Summary, AnalyticsHeatmap Heatmap, double[] SlotBusy, double[] SlotCapacity);

    private static async Task<OccupancyResult> OccupancyAsync(CmsDbContext db, AnalyticsPeriod period, TimeZoneInfo zone, List<Slot> slots,
        DateTimeOffset now, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.Status == DeviceStatuses.Approved)
            .Select(d => new { d.Id, d.ZoneId }).ToListAsync(ct);
        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.SortOrder).ThenBy(z => z.Name).ToListAsync(ct);
        var zoneIds = zones.Select(z => z.Id).ToHashSet();
        var noZone = Guid.Empty;
        // A PC without a (known) zone counts in the group "no zone"
        var deviceZone = devices.ToDictionary(d => d.Id, d => d.ZoneId is { } z && zoneIds.Contains(z) ? z : noZone);

        // Play time per PC: the session segments that overlap the period (an open segment runs until now
        // or the planned end of a prepaid session, whichever is first)
        var query = from seg in db.SessionSegments.AsNoTracking()
                    join s in db.Sessions.AsNoTracking() on seg.SessionId equals s.Id
                    select new { seg.DeviceId, seg.StartUtc, seg.EndUtc, SessionEnd = s.EndUtc };
        if (db.Database.IsNpgsql())
            query = query.Where(x => x.StartUtc < period.EndUtc && (x.EndUtc == null || x.EndUtc > period.FromUtc));
        var segments = await query.ToListAsync(ct);

        var heatBusy = new double[7, 24];
        var heatCapacity = new double[7, 24];
        var slotBusy = new double[slots.Count];
        var slotCapacity = new double[slots.Count];
        var zoneBusy = new Dictionary<Guid, double>();

        foreach (var group in segments.Where(s => deviceZone.ContainsKey(s.DeviceId)).GroupBy(s => s.DeviceId))
        {
            var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var s in group)
            {
                var end = s.EndUtc ?? (s.SessionEnd is { } planned && planned < now ? planned : now);
                var a = s.StartUtc > period.FromUtc ? s.StartUtc : period.FromUtc;
                var b = end < period.EndUtc ? end : period.EndUtc;
                if (b > a) intervals.Add((a, b));
            }
            var key = deviceZone[group.Key];
            foreach (var (start, end) in Merge(intervals))
            {
                zoneBusy[key] = (zoneBusy.TryGetValue(key, out var z) ? z : 0) + (end - start).TotalMinutes;
                ByClubHour(start, end, zone, (at, day, hour, minutes) =>
                {
                    heatBusy[day, hour] += minutes;
                    var i = SlotIndex(slots, at);
                    if (i >= 0) slotBusy[i] += minutes;
                });
            }
        }

        var pcs = devices.Count;
        var periodMinutes = 0.0;
        ByClubHour(period.FromUtc, period.EndUtc, zone, (at, day, hour, minutes) =>
        {
            periodMinutes += minutes;
            heatCapacity[day, hour] += minutes * pcs;
            var i = SlotIndex(slots, at);
            if (i >= 0) slotCapacity[i] += minutes * pcs;
        });

        var zoneRows = new List<ZoneOccupancy>();
        foreach (var z in zones)
        {
            var count = deviceZone.Values.Count(id => id == z.Id);
            if (count == 0) continue;
            var busy = zoneBusy.TryGetValue(z.Id, out var b) ? b : 0;
            zoneRows.Add(new ZoneOccupancy(z.Id, z.Name, z.Color, count, Hours(busy), Hours(periodMinutes * count), Percent(busy, periodMinutes * count)));
        }
        var unzoned = deviceZone.Values.Count(id => id == noZone);
        if (unzoned > 0)
        {
            var busy = zoneBusy.TryGetValue(noZone, out var b) ? b : 0;
            zoneRows.Add(new ZoneOccupancy(null, "", "#8b8fa3", unzoned, Hours(busy), Hours(periodMinutes * unzoned), Percent(busy, periodMinutes * unzoned)));
        }

        var allBusy = zoneBusy.Values.Sum();
        var percent = new List<IReadOnlyList<decimal?>>();
        decimal? max = null;
        for (var d = 0; d < 7; d++)
        {
            var row = new decimal?[24];
            for (var h = 0; h < 24; h++)
            {
                row[h] = Percent(heatBusy[d, h], heatCapacity[d, h]);
                if (row[h] is { } v && (max == null || v > max)) max = v;
            }
            percent.Add(row);
        }
        return new OccupancyResult(
            new AnalyticsOccupancy(Percent(allBusy, periodMinutes * pcs), pcs, Hours(allBusy), Hours(periodMinutes * pcs), zoneRows),
            new AnalyticsHeatmap(percent, max), slotBusy, slotCapacity);
    }

    private static decimal Hours(double minutes) => Math.Round((decimal)(minutes / 60), 1);

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Merge(List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        (DateTimeOffset Start, DateTimeOffset End)? current = null;
        foreach (var i in intervals.OrderBy(x => x.Start))
        {
            if (current is { } c && i.Start <= c.End)
            {
                current = (c.Start, i.End > c.End ? i.End : c.End);
                continue;
            }
            if (current != null) yield return current.Value;
            current = i;
        }
        if (current != null) yield return current.Value;
    }

    // ===== Bar items, shifts, reconciliation =====

    private static async Task<IReadOnlyList<ZBarItem>> TopItemsAsync(CmsDbContext db, List<LedgerEntryEntity> rows, CancellationToken ct)
    {
        var kinds = rows.Where(r => r.SaleId != null && r.Type is LedgerTypes.BarSale or LedgerTypes.BarRefund)
            .GroupBy(r => r.SaleId!.Value).ToDictionary(g => g.Key, g => g.First().Type == LedgerTypes.BarRefund);
        if (kinds.Count == 0) return Array.Empty<ZBarItem>();
        var ids = kinds.Keys.ToList();
        var items = await db.SaleItems.AsNoTracking().Where(i => ids.Contains(i.SaleId)).ToListAsync(ct);
        return items.GroupBy(i => (i.Name, i.Category))
            .Select(g => new ZBarItem(g.Key.Name, g.Key.Category,
                g.Sum(i => kinds[i.SaleId] ? -i.Quantity : i.Quantity),
                g.Sum(i => kinds[i.SaleId] ? -i.LineTotal : i.LineTotal)))
            .Where(i => i.Quantity != 0 || i.Amount != 0)
            .OrderByDescending(i => i.Amount).ThenBy(i => i.Name).Take(TopItems).ToList();
    }

    /// <summary>Shifts closed in the period and their cash variance (counted - expected).</summary>
    private static async Task<AnalyticsShifts> ShiftsAsync(CmsDbContext db, AnalyticsPeriod period, CancellationToken ct)
    {
        var closed = (await db.Shifts.AsNoTracking().Where(s => s.Status == ShiftStatuses.Closed).ToListAsync(ct))
            .Where(s => s.ClosedUtc is { } at && period.Contains(at)).OrderBy(s => s.Id).ToList();
        var variances = closed.Select(s => s.Variance ?? 0m).ToList();
        return new AnalyticsShifts(closed.Count, variances.Sum(), -variances.Where(v => v < 0).Sum(), variances.Where(v => v > 0).Sum(),
            closed.Where(s => (s.Variance ?? 0m) != 0m).OrderByDescending(s => s.ClosedUtc)
                .Select(s => new ShiftVariance(s.Id, s.ClosedUtc!.Value, s.ClosedByUsername, s.Variance!.Value, s.CloseNote)).ToList());
    }

    private static async Task<AnalyticsCheck> CheckAsync(CmsDbContext db, AnalyticsPeriod period, List<LedgerEntryEntity> rows, LedgerTotals total,
        DateTimeOffset now, CancellationToken ct)
    {
        // Shifts that lie fully in the period; their Z-report sums come from ALL their ledger rows
        var inside = (await db.Shifts.AsNoTracking().ToListAsync(ct))
            .Where(s => s.OpenedUtc >= period.FromUtc && (s.ClosedUtc ?? now) <= period.EndUtc)
            .Select(s => s.Id).ToHashSet();
        var ids = inside.ToList();
        var shiftRows = ids.Count == 0 ? new List<LedgerEntryEntity>()
            : await db.Ledger.AsNoTracking().Where(l => l.ShiftId != null && ids.Contains(l.ShiftId.Value)).ToListAsync(ct);
        var z = LedgerTotals.Of(shiftRows);
        var outside = LedgerTotals.Of(rows.Where(r => r.ShiftId is not { } id || !inside.Contains(id)));

        static AnalyticsSums Sums(LedgerTotals t) => new(t.TimeTotal, t.BarNet, t.CashNet, t.CardNet, t.Rows);
        var addsUp = z.TimeTotal + outside.TimeTotal == total.TimeTotal && z.BarNet + outside.BarNet == total.BarNet &&
                     z.CashNet + outside.CashNet == total.CashNet && z.CardNet + outside.CardNet == total.CardNet &&
                     z.Rows + outside.Rows == total.Rows;
        var methodsAddUp = total.Revenue.Total == total.RevenueTotal;
        return new AnalyticsCheck(total.Rows, total.FirstId, total.LastId, total.Reconciled && methodsAddUp, inside.Count, Sums(z), Sums(outside), addsUp);
    }
}
