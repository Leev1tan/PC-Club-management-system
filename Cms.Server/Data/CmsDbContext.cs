using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Data;

public class CmsDbContext : DbContext
{
    public CmsDbContext(DbContextOptions<CmsDbContext> options) : base(options) { }

    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<HeartbeatEntity> Heartbeats => Set<HeartbeatEntity>();

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
        });

        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceId).IsRequired();
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HeartbeatEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeviceId).IsRequired();
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.DeviceId, e.CreatedUtc });
        });
    }
}

public class DeviceEntity
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string DeviceKey { get; set; } = string.Empty;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? LastIp { get; set; }
}

public class SessionEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
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

