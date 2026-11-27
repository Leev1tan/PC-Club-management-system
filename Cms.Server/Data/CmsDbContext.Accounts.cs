using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Data;

public partial class CmsDbContext
{
    public DbSet<GameAccountEntity> GameAccounts => Set<GameAccountEntity>();
    public DbSet<GameAccountLeaseEntity> GameAccountLeases => Set<GameAccountLeaseEntity>();

    public const string OpenLeaseOfAccountIndex = "IX_GameAccountLeases_AccountId_Open";
    public const string OpenLeaseOfPcIndex = "IX_GameAccountLeases_DeviceId_Platform_Open";

    private static void ConfigureAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameAccountEntity>(entity =>
        {
            entity.ToTable("GameAccounts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Platform).HasMaxLength(20).IsRequired();
            entity.Property(e => e.LoginMask).HasMaxLength(40).IsRequired();
            entity.Property(e => e.LoginCipher).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.SecretCipher).HasMaxLength(4000);
            entity.Property(e => e.CreatedByUsername).HasMaxLength(64);
            entity.HasIndex(e => e.Platform);
            entity.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();
        });

        modelBuilder.Entity<GameAccountLeaseEntity>(entity =>
        {
            entity.ToTable("GameAccountLeases");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Platform).HasMaxLength(20).IsRequired();
            entity.Property(e => e.EndReason).HasMaxLength(20);
            entity.Property(e => e.EndedByUsername).HasMaxLength(64);
            entity.HasOne<GameAccountEntity>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<DeviceEntity>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
            // One open lease per account and one open lease per PC and platform, enforced by the database (partial unique indexes);
            // SessionId, UserId and GameId are plain ids (the demo reset truncates sessions and games)
            entity.HasIndex(e => e.AccountId).IsUnique().HasFilter("\"EndedUtc\" IS NULL").HasDatabaseName(OpenLeaseOfAccountIndex);
            entity.HasIndex(e => new { e.DeviceId, e.Platform }).IsUnique().HasFilter("\"EndedUtc\" IS NULL").HasDatabaseName(OpenLeaseOfPcIndex);
            entity.HasIndex(e => e.SessionId);
        });

        modelBuilder.Entity<GameEntity>().Property(e => e.AccountPlatform).HasMaxLength(20);
    }
}

/// <summary>
/// A club account of a game platform (spec area 8): the login and the secret are encrypted with ASP.NET Core Data
/// Protection (purpose Cms.GameAccounts.Credentials.v1) and never leave the server except to the agent of the PC that
/// leases the account, once per lease. The API shows only LoginMask and whether a secret is stored. A removed account
/// keeps its row (lease history) without the encrypted data.
/// </summary>
public class GameAccountEntity
{
    public Guid Id { get; set; }
    /// <summary>steam, epic, battlenet, riot, ea, ubisoft, gog or other (Accounts/AccountPlatforms).</summary>
    public string Platform { get; set; } = string.Empty;
    /// <summary>The login with most characters hidden ("de•••01"), for staff.</summary>
    public string LoginMask { get; set; } = string.Empty;
    /// <summary>The protected login (empty after the removal).</summary>
    public string LoginCipher { get; set; } = string.Empty;
    /// <summary>The protected secret; null = no secret (the account is never leased then).</summary>
    public string? SecretCipher { get; set; }
    /// <summary>Off: never leased (for example while staff check the account).</summary>
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? SecretUpdatedUtc { get; set; }
    public DateTimeOffset? RemovedUtc { get; set; }
    public string? CreatedByUsername { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>
/// An account given to a PC for one session (spec area 8): from the moment the player opens a game that needs it
/// until the session ends, the player logs out, staff free it or the server finds the session gone. DeliveredUtc:
/// when the agent of the PC fetched the credentials (once per lease).
/// </summary>
public class GameAccountLeaseEntity
{
    public long Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid DeviceId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? GameId { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    /// <summary>session_end, transfer, staff, removed, recovery.</summary>
    public string? EndReason { get; set; }
    public string? EndedByUsername { get; set; }
}
