using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Data;

public class CmsDbContext : DbContext
{
    public CmsDbContext(DbContextOptions<CmsDbContext> options) : base(options) { }

    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<HeartbeatEntity> Heartbeats => Set<HeartbeatEntity>();
    public DbSet<CommandEntity> Commands => Set<CommandEntity>();
    public DbSet<ZoneEntity> Zones => Set<ZoneEntity>();
    public DbSet<TariffEntity> Tariffs => Set<TariffEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<TransactionEntity> Transactions => Set<TransactionEntity>();
    public DbSet<StaffUserEntity> StaffUsers => Set<StaffUserEntity>();
    public DbSet<ClubSettingEntity> ClubSettings => Set<ClubSettingEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    public const string ActiveSessionIndex = "IX_Sessions_DeviceId_Active";

    /// <summary>
    /// Ends all but the newest active session of each PC. Runs before the unique index
    /// IX_Sessions_DeviceId_Active is created on an existing database (PostgreSQL migration, SQLite upgrade).
    /// </summary>
    public const string EndDuplicateActiveSessionsSql =
        "UPDATE \"Sessions\" SET \"Status\" = 'ended' WHERE \"Status\" = 'active' AND EXISTS (" +
        "SELECT 1 FROM \"Sessions\" AS o WHERE o.\"DeviceId\" = \"Sessions\".\"DeviceId\" AND o.\"Status\" = 'active' " +
        "AND (o.\"StartUtc\" > \"Sessions\".\"StartUtc\" OR (o.\"StartUtc\" = \"Sessions\".\"StartUtc\" AND o.\"Id\" > \"Sessions\".\"Id\")))";

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        // The audit log is append-only (the database also refuses UPDATE and DELETE with a trigger)
        foreach (var entry in ChangeTracker.Entries<AuditLogEntity>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("The audit log is append-only: entries cannot be changed or deleted.");
        }

        // Every change of a session gets a new stamp; EF checks the old one in the UPDATE
        foreach (var entry in ChangeTracker.Entries<SessionEntity>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.ConcurrencyStamp = Guid.NewGuid();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Hostname).HasMaxLength(255).IsRequired();
            entity.Property(e => e.OsVersion).HasMaxLength(255);
            entity.Property(e => e.AgentVersion).HasMaxLength(50);
            entity.Property(e => e.DeviceKey).HasMaxLength(500).IsRequired();
            entity.HasIndex(e => e.DeviceKey).IsUnique();
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired().HasDefaultValue("approved");
            entity.HasOne<ZoneEntity>().WithMany().HasForeignKey(e => e.ZoneId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ZoneEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Color).HasMaxLength(20);
        });

        modelBuilder.Entity<TariffEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PricePerHour).HasColumnType("decimal(10,2)");
            entity.HasOne<ZoneEntity>().WithMany().HasForeignKey(e => e.ZoneId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.PasswordHash).HasMaxLength(500);
            entity.Property(e => e.Balance).HasColumnType("decimal(10,2)");
            entity.Property(e => e.BonusPoints).HasColumnType("decimal(10,2)");
        });

        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceId).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            entity.Property(e => e.TotalCost).HasColumnType("decimal(10,2)");
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<TariffEntity>().WithMany().HasForeignKey(e => e.TariffId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<UserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => new { e.DeviceId, e.Status });
            // At most ONE active session per PC, enforced by the database (partial unique index)
            entity.HasIndex(e => e.DeviceId)
                .IsUnique()
                .HasFilter("\"Status\" = 'active'")
                .HasDatabaseName(ActiveSessionIndex);
            // Optimistic concurrency: a session changed by someone else in between cannot be saved
            entity.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();
        });

        modelBuilder.Entity<HeartbeatEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceId).IsRequired();
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.DeviceId, e.CreatedUtc });
            entity.HasIndex(e => e.CreatedUtc); // retention clean-up
        });

        modelBuilder.Entity<CommandEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceId).IsRequired();
            entity.Property(e => e.Type).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.DeviceId, e.Status });
            entity.HasIndex(e => new { e.Status, e.CreatedUtc }); // retention clean-up
        });

        modelBuilder.Entity<TransactionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Type).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.HasOne<UserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.CreatedUtc);
        });

        modelBuilder.Entity<StaffUserEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(20).IsRequired();
            entity.Property(e => e.SecurityStamp).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<AuditLogEntity>(entity =>
        {
            entity.ToTable("AuditLog");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.ActorUsername).HasMaxLength(64);
            entity.Property(e => e.ActorRole).HasMaxLength(20);
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.TargetType).HasMaxLength(32);
            entity.Property(e => e.TargetId).HasMaxLength(64);
            entity.Property(e => e.Summary).HasMaxLength(500);
            entity.Property(e => e.DetailsJson).HasMaxLength(4000);
            entity.Property(e => e.Ip).HasMaxLength(64);
            entity.HasIndex(e => e.Action);
        });

        modelBuilder.Entity<ClubSettingEntity>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(100);
            entity.Property(e => e.Value).HasMaxLength(4000);
            entity.Property(e => e.UpdatedBy).HasMaxLength(64);
        });
    }
}

// ===== Entities =====

public class DeviceEntity
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    /// <summary>"sha256:" + SHA-256 of the device key (see Auth/DeviceKeys); never the key itself.</summary>
    public string DeviceKey { get; set; } = string.Empty;
    /// <summary>pending (waits for staff approval), approved, revoked.</summary>
    public string Status { get; set; } = "approved";
    public DateTimeOffset? RegisteredUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? LastIp { get; set; }
    public Guid? ZoneId { get; set; }
    public int? PositionX { get; set; }  // For PC map grid
    public int? PositionY { get; set; }
}

public class ZoneEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#6366f1"; // Default indigo
    public int SortOrder { get; set; }
}

public class TariffEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PricePerHour { get; set; }
    public bool IsDefault { get; set; }
    public Guid? ZoneId { get; set; }  // Optional: zone-specific tariff
}

public class UserEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public decimal BonusPoints { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class SessionEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? TariffId { get; set; }
    public Guid? UserId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public string Status { get; set; } = "active"; // active, paused, ended
    public decimal TotalCost { get; set; }
    public bool IsPrepaid { get; set; }  // true = fixed duration, false = open-ended (pay on exit)
    /// <summary>Concurrency token; changes on every update (see CmsDbContext.BeforeSave).</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public class HeartbeatEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public double CpuPercent { get; set; }
    public double MemPercent { get; set; }
    public string? ActiveUser { get; set; }
    public string? Ip { get; set; }
}

public class CommandEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public string Status { get; set; } = "pending"; // pending, delivered, done, failed, ignored, expired
    public string? Result { get; set; }
}

public class TransactionEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public decimal Amount { get; set; }
    public string Type { get; set; } = string.Empty; // topup, session_charge, bonus
    public string? Description { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Admin UI / API account of a club employee (not a player).</summary>
public class StaffUserEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;   // lower case
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;       // Owner, Manager, Cashier, Tech
    public bool IsActive { get; set; } = true;
    /// <summary>Changes on password, role or status change; invalidates existing login cookies.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginUtc { get; set; }
}

/// <summary>
/// Immutable record of a staff action (who, when, what, from which IP). Append-only: no API changes
/// or deletes entries, SaveChanges refuses it, and a database trigger blocks UPDATE and DELETE.
/// The actor name and role are copied, so the entry stays readable when the account changes.
/// </summary>
public class AuditLogEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorUsername { get; set; }
    public string? ActorRole { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Summary { get; set; }
    public string? DetailsJson { get; set; }
    public string? Ip { get; set; }
}

/// <summary>Club-wide settings that the owner changes at run time (key/value).</summary>
public class ClubSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}

public static class ClubSettingKeys
{
    /// <summary>Written once when the first owner account is created (guards the setup screen).</summary>
    public const string SetupCompleted = "setup.completed";
}
