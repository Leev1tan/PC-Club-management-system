using Cms.Server.Reservations;
using Xunit;

namespace Cms.Server.Tests;

public sealed class ReservationPlanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 28, 16, 0, 0, TimeSpan.Zero);
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static DateTimeOffset H(double hours) => T0.AddHours(hours);

    [Fact]
    public void Pc_reservations_never_overlap_on_one_pc()
    {
        Assert.NotNull(ReservationPlan.Assign(new[] { A }, new[] { new PlanItem(1, A, H(0), H(2)), new PlanItem(2, A, H(2), H(3)) }));
        Assert.Null(ReservationPlan.Assign(new[] { A }, new[] { new PlanItem(1, A, H(0), H(2)), new PlanItem(2, A, H(1.5), H(3)) }));
    }

    [Fact]
    public void Any_pc_reservations_fit_the_free_pcs_of_the_zone()
    {
        var items = new List<PlanItem> { new(1, A, H(0), H(2)), new(2, null, H(1), H(3)) };
        var plan = ReservationPlan.Assign(new[] { A, B }, items);
        Assert.Equal(B, plan![2]);
        items.Add(new PlanItem(3, null, H(2.5), H(4)));
        Assert.Equal(A, ReservationPlan.Assign(new[] { A, B }, items)![3]); // A is free from 2:00
        items.Add(new PlanItem(4, null, H(1.5), H(2.5)));
        Assert.Null(ReservationPlan.Assign(new[] { A, B }, items));        // three at 1:30-2:00 on two PCs
    }

    [Fact]
    public void A_pc_reservation_that_leaves_no_pc_for_an_any_pc_reservation_is_refused()
    {
        // B is taken from 1:00, A from 2:00: the any-PC reservation 0:30-2:30 has no PC for the whole time
        var items = new[] { new PlanItem(1, A, H(2), H(3)), new PlanItem(2, B, H(1), H(3)), new PlanItem(3, null, H(0.5), H(2.5)) };
        Assert.Null(ReservationPlan.Assign(new[] { A, B }, items));
    }

    [Fact]
    public void Free_capacity_counts_the_any_pc_reservations_that_still_fit()
    {
        var pcs = new[] { A, B };
        Assert.Equal(2, ReservationPlan.FreeCapacity(pcs, Array.Empty<PlanItem>(), H(0), H(2)));
        Assert.Equal(1, ReservationPlan.FreeCapacity(pcs, new[] { new PlanItem(1, A, H(1), H(2)) }, H(0), H(2)));
        Assert.Equal(2, ReservationPlan.FreeCapacity(pcs, new[] { new PlanItem(1, A, H(1), H(2)) }, H(2), H(4)));
        Assert.Equal(0, ReservationPlan.FreeCapacity(pcs, new[] { new PlanItem(1, null, H(0), H(5)), new PlanItem(2, B, H(3), H(4)) }, H(3), H(4)));
    }
}
