namespace Cms.Server.Reservations;

/// <summary>A reservation (or a busy time) in a plan: Pc = its PC, or null = any PC of the zone.</summary>
public sealed record PlanItem(long Id, Guid? Pc, DateTimeOffset Start, DateTimeOffset End)
{
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => Start < end && start < End;
}

/// <summary>
/// Assignment of reservations to the PCs of a zone. Items with a PC keep it; an any-PC item gets a PC on which it
/// overlaps nothing. Greedy in start order, best fit (the PC that became free last before the start), so it may
/// refuse a rare set that a perfect search could place: it never over-books. A new reservation is accepted only
/// when the plan with it still places every item; the same plan tells how many more any-PC reservations fit
/// (the free capacity of the public booking page).
/// </summary>
public static class ReservationPlan
{
    /// <summary>PC of every item (id -> PC), or null when the items do not fit on the PCs.</summary>
    public static Dictionary<long, Guid>? Assign(IReadOnlyList<Guid> pcs, IEnumerable<PlanItem> items)
    {
        var list = items.ToList();
        var result = new Dictionary<long, Guid>();
        var busy = new Dictionary<Guid, List<PlanItem>>();
        foreach (var fixedItem in list.Where(i => i.Pc.HasValue))
        {
            var pc = fixedItem.Pc!.Value;
            var onPc = busy.TryGetValue(pc, out var l) ? l : busy[pc] = new List<PlanItem>();
            if (onPc.Any(o => o.Overlaps(fixedItem.Start, fixedItem.End))) return null;
            onPc.Add(fixedItem);
            result[fixedItem.Id] = pc;
        }
        foreach (var floating in list.Where(i => !i.Pc.HasValue).OrderBy(i => i.Start).ThenBy(i => i.End).ThenBy(i => i.Id))
        {
            Guid? best = null;
            var bestFreeSince = DateTimeOffset.MinValue;
            foreach (var pc in pcs)
            {
                var onPc = busy.TryGetValue(pc, out var l) ? l : null;
                if (onPc != null && onPc.Any(o => o.Overlaps(floating.Start, floating.End))) continue;
                var freeSince = onPc?.Where(o => o.End <= floating.Start).Select(o => o.End).DefaultIfEmpty(DateTimeOffset.MinValue).Max() ?? DateTimeOffset.MinValue;
                if (best == null || freeSince > bestFreeSince)
                {
                    best = pc;
                    bestFreeSince = freeSince;
                }
            }
            if (best == null) return null;
            (busy.TryGetValue(best.Value, out var b) ? b : busy[best.Value] = new List<PlanItem>()).Add(floating);
            result[floating.Id] = best.Value;
        }
        return result;
    }

    /// <summary>How many more any-PC reservations of [start, end) fit on the PCs with these items (0 to the number of PCs).</summary>
    public static int FreeCapacity(IReadOnlyList<Guid> pcs, IReadOnlyList<PlanItem> items, DateTimeOffset start, DateTimeOffset end)
    {
        var relevant = items.ToList();
        var extra = 0;
        while (extra < pcs.Count)
        {
            relevant.Add(new PlanItem(-1 - extra, null, start, end));
            if (Assign(pcs, relevant) == null) break;
            extra++;
        }
        return extra;
    }
}
