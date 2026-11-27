using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Data;

public partial class CmsDbContext
{
    public const string ActiveEventPlayerIndex = "IX_EventPlayers_EventId_UserId_Active";
    public const string EventMatchSlotIndex = "IX_EventMatches_EventId_Round_Slot";

    private static void ConfigureEvents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable("Events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(EventLimits.NameLength).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.PriceMode).HasMaxLength(8).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(10,2)");
            entity.Property(e => e.EntryFee).HasColumnType("decimal(10,2)");
            entity.Property(e => e.CancelReason).HasMaxLength(200);
            entity.Property(e => e.CreatedByUsername).HasMaxLength(64);
            entity.HasOne<ZoneEntity>().WithMany().HasForeignKey(e => e.ZoneId).OnDelete(DeleteBehavior.SetNull);
            // Club day of the start (DateOnly.DayNumber): SQLite cannot compare DateTimeOffset in SQL
            entity.HasIndex(e => new { e.Status, e.StartDay });
            entity.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();
        });

        modelBuilder.Entity<EventDeviceEntity>(entity =>
        {
            entity.ToTable("EventDevices");
            entity.HasKey(e => new { e.EventId, e.DeviceId });
            entity.HasOne<EventEntity>().WithMany().HasForeignKey(e => e.EventId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.DeviceId);
        });

        modelBuilder.Entity<EventTeamEntity>(entity =>
        {
            entity.ToTable("EventTeams");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(EventLimits.TeamNameLength).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CreatedByUsername).HasMaxLength(64);
            entity.HasOne<EventEntity>().WithMany().HasForeignKey(e => e.EventId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.EventId);
        });

        modelBuilder.Entity<EventPlayerEntity>(entity =>
        {
            entity.ToTable("EventPlayers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Fee).HasColumnType("decimal(10,2)");
            entity.Property(e => e.FeeStatus).HasMaxLength(12).IsRequired();
            entity.Property(e => e.StartError).HasMaxLength(40);
            entity.HasOne<EventEntity>().WithMany().HasForeignKey(e => e.EventId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<EventTeamEntity>().WithMany().HasForeignKey(e => e.TeamId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: a player with an entry fee has ledger rows and cannot be deleted anyway
            entity.HasOne<UserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.SetNull);
            // A player is in one team of an event (database index); SessionId is a plain id (the demo reset truncates sessions)
            entity.HasIndex(e => new { e.EventId, e.UserId }).IsUnique().HasFilter("\"Status\" = 'active'").HasDatabaseName(ActiveEventPlayerIndex);
            entity.HasIndex(e => e.TeamId);
            entity.HasIndex(e => e.SessionId);
        });

        modelBuilder.Entity<EventMatchEntity>(entity =>
        {
            entity.ToTable("EventMatches");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.DecidedByUsername).HasMaxLength(64);
            entity.HasOne<EventEntity>().WithMany().HasForeignKey(e => e.EventId).OnDelete(DeleteBehavior.Cascade);
            // Team ids are plain ids: a match keeps them when a team is withdrawn
            entity.HasIndex(e => new { e.EventId, e.Round, e.Slot }).IsUnique().HasDatabaseName(EventMatchSlotIndex);
        });
    }
}

public static class EventStatuses
{
    /// <summary>Created; the PCs are booked for the event time, teams register.</summary>
    public const string Scheduled = "scheduled";
    /// <summary>Started: the PCs of the registered players run event sessions with the common end.</summary>
    public const string Running = "running";
    /// <summary>Over (at the end or finished early by staff): the entry fees are kept.</summary>
    public const string Finished = "finished";
    /// <summary>Cancelled by staff (or never started before its end): the entry fees went back to the balances.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>These book their PCs for their time.</summary>
    public static readonly string[] Open = { Scheduled, Running };
}

public static class EventPriceModes
{
    /// <summary>The PC time of the event costs nothing (audited as free time of the event).</summary>
    public const string Free = "free";
    /// <summary>Every player pays a fixed price for the PC time of the event, from the balances at the start.</summary>
    public const string Fixed = "fixed";
}

public static class EventFeeStatuses
{
    public const string None = "none";
    /// <summary>Taken off the real balance at the registration and held (ledger event_fee_hold).</summary>
    public const string Held = "held";
    /// <summary>Back on the balance: the team withdrew before the start, or the event was cancelled (event_fee_release).</summary>
    public const string Released = "released";
    /// <summary>Kept by the club when the event is over (event_fee_kept, revenue).</summary>
    public const string Kept = "kept";
}

public static class EventMemberStatuses
{
    public const string Active = "active";
    public const string Withdrawn = "withdrawn";
}

public static class EventLimits
{
    public const int NameLength = 80;
    public const int TeamNameLength = 40;
    public const int MaxTeamSize = 10;
    public const int MinMinutes = 30;
    public const int MaxMinutes = 12 * 60;
    public const int StepMinutes = 15;
    public const decimal MaxPrice = 10_000m;
    public const decimal MaxFee = 10_000m;
    public const int MaxPcs = 100;
    public const int MaxDaysAhead = 90;
}

/// <summary>
/// A tournament or club event (spec area 9): a set of PCs (a zone or chosen PCs) is booked for a time; registered
/// players or teams of TeamSize players sign up (each player may pay an entry fee from the real balance, held until
/// the event is over); at the start the PCs of the players are unlocked together with a common end; at the end the
/// PCs lock again. A single-elimination bracket with results entered by staff. Names of events and teams are shown
/// on the lock screens and in the admin UI, never written into the audit log (ids only).
/// </summary>
public class EventEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = EventStatuses.Scheduled;
    /// <summary>The zone the PCs were chosen from (display); the PCs are in EventDevices.</summary>
    public Guid? ZoneId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public int DurationMinutes { get; set; }
    /// <summary>Club-local date of the start as DateOnly.DayNumber.</summary>
    public int StartDay { get; set; }
    /// <summary>free or fixed (EventPriceModes).</summary>
    public string PriceMode { get; set; } = EventPriceModes.Free;
    /// <summary>Fixed price of the PC time per player (PriceMode fixed).</summary>
    public decimal Price { get; set; }
    /// <summary>Entry fee per player from the real balance (0 = none).</summary>
    public decimal EntryFee { get; set; }
    /// <summary>Players per team (1 = every player plays alone).</summary>
    public int TeamSize { get; set; } = 1;
    public DateTimeOffset? StartedUtc { get; set; }
    /// <summary>When the event finished or was cancelled.</summary>
    public DateTimeOffset? EndedUtc { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>The team that won the final of the bracket.</summary>
    public long? WinnerTeamId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public Guid? CreatedById { get; set; }
    public string? CreatedByUsername { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>A PC booked for an event.</summary>
public class EventDeviceEntity
{
    public long EventId { get; set; }
    public Guid DeviceId { get; set; }
}

/// <summary>A team of an event (one player for TeamSize 1); Seed = the order of the registration (bracket seeding).</summary>
public class EventTeamEntity
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? CaptainUserId { get; set; }
    public int Seed { get; set; }
    public string Status { get; set; } = EventMemberStatuses.Active;
    public DateTimeOffset CreatedUtc { get; set; }
    public string? CreatedByUsername { get; set; }
}

/// <summary>A registered player of an event: the team, the PC (seat), the session of the event, the entry fee.</summary>
public class EventPlayerEntity
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long TeamId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>The PC of the player (given at the registration, neighbours for a team).</summary>
    public Guid? DeviceId { get; set; }
    /// <summary>The event session of the player (set at the start).</summary>
    public Guid? SessionId { get; set; }
    public string Status { get; set; } = EventMemberStatuses.Active;
    public decimal Fee { get; set; }
    public string FeeStatus { get; set; } = EventFeeStatuses.None;
    /// <summary>Why the session of the player did not start (error code), for the staff.</summary>
    public string? StartError { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>
/// A match of the single-elimination bracket: round 1 = the first round; slot = position in the round. The winner
/// moves to round + 1, slot / 2 (side A for an even slot). A match with one team only is a bye (decided at once).
/// </summary>
public class EventMatchEntity
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public int Round { get; set; }
    public int Slot { get; set; }
    public long? TeamAId { get; set; }
    public long? TeamBId { get; set; }
    public long? WinnerTeamId { get; set; }
    public int? ScoreA { get; set; }
    public int? ScoreB { get; set; }
    public DateTimeOffset? DecidedUtc { get; set; }
    public string? DecidedByUsername { get; set; }
}
