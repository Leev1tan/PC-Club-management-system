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
    public DbSet<TariffRuleEntity> TariffRules => Set<TariffRuleEntity>();
    public DbSet<PackageEntity> Packages => Set<PackageEntity>();
    public DbSet<SessionSegmentEntity> SessionSegments => Set<SessionSegmentEntity>();
    public DbSet<LedgerEntryEntity> Ledger => Set<LedgerEntryEntity>();
    public DbSet<ShiftEntity> Shifts => Set<ShiftEntity>();
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<StockMovementEntity> StockMovements => Set<StockMovementEntity>();
    public DbSet<SaleEntity> Sales => Set<SaleEntity>();
    public DbSet<SaleItemEntity> SaleItems => Set<SaleItemEntity>();

    /// <summary>
    /// Cash and card rows of the ledger need an open shift (Shifts:RequireOpenShift, default true).
    /// Set by dependency injection; a context created with <c>new</c> (tools, unit tests) does not check.
    /// </summary>
    public bool RequireOpenShift { get; init; }

    /// <summary>One-time jobs that copy old history (ledger backfill, demo seed) set this to skip the shift check.</summary>
    internal bool SkipShiftCheck { get; set; }

    public CmsDbContext(DbContextOptions<CmsDbContext> options, ShiftOptions shiftOptions) : base(options)
    {
        RequireOpenShift = shiftOptions.RequireOpenShift;
    }

    private Cms.Server.Billing.ClubCurrency? _currency;

    /// <summary>Club currency (setting club.currency, default UAH), read once per context (one request).</summary>
    public async Task<Cms.Server.Billing.ClubCurrency> CurrencyAsync(CancellationToken ct = default) =>
        _currency ??= await Cms.Server.Billing.ClubCurrency.LoadAsync(this, ct);

    /// <summary>Symbol of the club currency for texts the server writes (ledger descriptions, audit summaries).</summary>
    public async Task<string> CurrencySymbolAsync(CancellationToken ct = default) => (await CurrencyAsync(ct)).Symbol;

    internal void SetCurrency(Cms.Server.Billing.ClubCurrency currency) => _currency = currency;

    public const string ActiveSessionIndex = "IX_Sessions_DeviceId_Active";
    public const string OpenShiftIndex = "IX_Shifts_Open";
    public const string RefundOfSaleIndex = "IX_Sales_RefundOfSaleId";

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

        // The money ledger is append-only too (database trigger as well); corrections are new rows
        foreach (var entry in ChangeTracker.Entries<LedgerEntryEntity>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("The ledger is append-only: entries cannot be changed or deleted.");
        }

        // Receipts and the stock log are append-only too (database triggers as well)
        foreach (var entry in ChangeTracker.Entries().Where(e => e.Entity is SaleEntity or SaleItemEntity or StockMovementEntity))
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException($"{entry.Metadata.GetTableName()} is append-only: rows cannot be changed or deleted.");
        }

        // A closed shift is final: nobody edits, reopens or deletes it (database trigger as well)
        foreach (var entry in ChangeTracker.Entries<ShiftEntity>())
        {
            if (entry.State == EntityState.Deleted ||
                (entry.State == EntityState.Modified && (string?)entry.OriginalValues[nameof(ShiftEntity.Status)] == ShiftStatuses.Closed))
                throw new InvalidOperationException("A closed shift cannot be changed, reopened or deleted.");
        }

        // Money at the desk (cash, card) belongs to a shift, so the Z-report of the shift shows it
        if (RequireOpenShift && !SkipShiftCheck)
        {
            foreach (var entry in ChangeTracker.Entries<LedgerEntryEntity>())
            {
                if (entry.State == EntityState.Added && entry.Entity.ShiftId == null && entry.Entity.DeskAmount != 0 &&
                    Cms.Server.Billing.PaymentMethods.IsDesk(entry.Entity.Method))
                    throw new ShiftRequiredException();
            }
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
            entity.Property(e => e.MinimumCharge).HasColumnType("decimal(10,2)");
            entity.HasOne<ZoneEntity>().WithMany().HasForeignKey(e => e.ZoneId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TariffRuleEntity>(entity =>
        {
            entity.ToTable("TariffRules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PricePerHour).HasColumnType("decimal(10,2)");
            entity.HasOne<TariffEntity>().WithMany().HasForeignKey(e => e.TariffId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.TariffId, e.SortOrder });
        });

        modelBuilder.Entity<PackageEntity>(entity =>
        {
            entity.ToTable("Packages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(10,2)");
            entity.HasOne<ZoneEntity>().WithMany().HasForeignKey(e => e.ZoneId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.ZoneId);
        });

        modelBuilder.Entity<SessionSegmentEntity>(entity =>
        {
            entity.ToTable("SessionSegments");
            entity.HasKey(e => e.Id);
            entity.HasOne<SessionEntity>().WithMany().HasForeignKey(e => e.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.SessionId);
        });

        modelBuilder.Entity<LedgerEntryEntity>(entity =>
        {
            entity.ToTable("LedgerEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Type).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Method).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Amount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.BalanceDelta).HasColumnType("decimal(10,2)");
            entity.Property(e => e.BonusDelta).HasColumnType("decimal(10,2)");
            entity.Property(e => e.DeskAmount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.BalanceAfter).HasColumnType("decimal(10,2)");
            entity.Property(e => e.BonusAfter).HasColumnType("decimal(10,2)");
            entity.Property(e => e.DebtDelta).HasColumnType("decimal(10,2)");
            entity.Property(e => e.DebtAfter).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.ActorUsername).HasMaxLength(64);
            // Restrict: a player with money history cannot be deleted (rows are never changed: no SET NULL / CASCADE)
            entity.HasOne<UserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.UserId, e.Id });
            entity.HasIndex(e => e.CreatedUtc);
            entity.HasIndex(e => e.SessionId);
            // Plain ids, no foreign keys: the ledger never changes, and the demo reset truncates it alone
            entity.HasIndex(e => e.ShiftId);
            entity.HasIndex(e => e.SaleId);
        });

        modelBuilder.Entity<ShiftEntity>(entity =>
        {
            entity.ToTable("Shifts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Status).HasMaxLength(16).IsRequired();
            entity.Property(e => e.OpenedByUsername).HasMaxLength(64);
            entity.Property(e => e.ClosedByUsername).HasMaxLength(64);
            entity.Property(e => e.OpeningFloat).HasColumnType("decimal(10,2)");
            entity.Property(e => e.CountedCash).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ExpectedCash).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Variance).HasColumnType("decimal(10,2)");
            entity.Property(e => e.OpenNote).HasMaxLength(300);
            entity.Property(e => e.CloseNote).HasMaxLength(300);
            entity.Property(e => e.AuditHeadHash).HasMaxLength(64);
            // At most ONE open shift per club, enforced by the database (partial unique index)
            entity.HasIndex(e => e.Status).IsUnique().HasFilter("\"Status\" = 'open'").HasDatabaseName(OpenShiftIndex);
        });

        modelBuilder.Entity<ProductEntity>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Price).HasColumnType("decimal(10,2)");
            entity.HasIndex(e => new { e.Category, e.SortOrder });
        });

        modelBuilder.Entity<StockMovementEntity>(entity =>
        {
            entity.ToTable("StockMovements");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Type).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.UnitCost).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ActorUsername).HasMaxLength(64);
            // Restrict: a product with stock history is taken off sale, never deleted
            entity.HasOne<ProductEntity>().WithMany().HasForeignKey(e => e.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.ProductId, e.Id });
            entity.HasIndex(e => e.ShiftId);
        });

        modelBuilder.Entity<SaleEntity>(entity =>
        {
            entity.ToTable("Sales");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Kind).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Method).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Total).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Reason).HasMaxLength(300);
            entity.Property(e => e.ActorUsername).HasMaxLength(64);
            entity.HasOne<UserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.ShiftId);
            // A sale is refunded at most once: the refund receipt points to it (partial unique index)
            entity.HasIndex(e => e.RefundOfSaleId).IsUnique().HasFilter("\"RefundOfSaleId\" IS NOT NULL").HasDatabaseName(RefundOfSaleIndex);
        });

        modelBuilder.Entity<SaleItemEntity>(entity =>
        {
            entity.ToTable("SaleItems");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(10,2)");
            entity.Property(e => e.LineTotal).HasColumnType("decimal(10,2)");
            entity.HasOne<SaleEntity>().WithMany().HasForeignKey(e => e.SaleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProductEntity>().WithMany().HasForeignKey(e => e.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.SaleId);
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
            entity.Property(e => e.Debt).HasColumnType("decimal(10,2)");
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
            entity.HasOne<PackageEntity>().WithMany().HasForeignKey(e => e.PackageId).OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.PaymentMethod).HasMaxLength(16);
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
            entity.Property(e => e.PrevHash).HasMaxLength(64);
            entity.Property(e => e.Hash).HasMaxLength(64);
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
    /// <summary>Base price; applies when no time-of-day rule matches (see TariffRuleEntity).</summary>
    public decimal PricePerHour { get; set; }
    public bool IsDefault { get; set; }
    public Guid? ZoneId { get; set; }  // Optional: zone-specific tariff
    /// <summary>Post-paid time is rounded up to this many minutes (1 = per minute; 0 in old databases = 1).</summary>
    public int BillingStepMinutes { get; set; } = 1;
    /// <summary>Smallest amount charged for a session that ran at all (0 = none).</summary>
    public decimal MinimumCharge { get; set; }
}

/// <summary>
/// Time-of-day / weekday price of a tariff, in club local time. Start and end are minutes after
/// midnight; End &lt;= Start means the window runs past midnight (22:00-08:00). Days is a bit mask
/// of the day the window STARTS on (bit 0 = Monday ... bit 6 = Sunday). The first matching rule
/// (lowest SortOrder) wins; without a match the tariff base price applies.
/// </summary>
public class TariffRuleEntity
{
    public Guid Id { get; set; }
    public Guid TariffId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DaysMask { get; set; } = 127;
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    public decimal PricePerHour { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Fixed-price package bound to a zone: a fixed duration (3 h, 5 h), a time window with a cut-off
/// (night pack 22:00-08:00), or both (morning pack: 3 h, sold 08:00-12:00, ends by 12:00 at the latest).
/// Window minutes as in TariffRuleEntity; DaysMask = days on which the window starts.
/// </summary>
public class PackageEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? ZoneId { get; set; }
    public decimal Price { get; set; }
    public int? DurationMinutes { get; set; }
    public int? WindowStartMinute { get; set; }
    public int? WindowEndMinute { get; set; }
    public int DaysMask { get; set; } = 127;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class UserEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Real money balance. Changed only together with a LedgerEntries row (see Billing/Wallet).</summary>
    public decimal Balance { get; set; }
    /// <summary>Bonus balance (cashback, promo): spendable on PC time only, never paid out or spent at the bar.</summary>
    public decimal BonusPoints { get; set; }
    /// <summary>Unpaid post-paid time (always >= 0). Changed only together with a ledger row (DebtDelta).</summary>
    public decimal Debt { get; set; }
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
    /// <summary>active or ended. A paused session stays "active" (it keeps the PC) and has PausedUtc set.</summary>
    public string Status { get; set; } = "active";
    public decimal TotalCost { get; set; }
    public bool IsPrepaid { get; set; }  // true = fixed duration or package, false = open-ended (pay on exit)
    /// <summary>Concurrency token; changes on every update (see CmsDbContext.BeforeSave).</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    /// <summary>Package bought for this session (null = tariff time).</summary>
    public Guid? PackageId { get; set; }
    /// <summary>Set while the session is paused: time stops, the PC is locked.</summary>
    public DateTimeOffset? PausedUtc { get; set; }
    /// <summary>Hard end of a package window (night pack 08:00): pause, transfer and extend never go past it.</summary>
    public DateTimeOffset? CutoffUtc { get; set; }
    /// <summary>How the session was paid: balance, cash, card (null for open-ended until it ends).</summary>
    public string? PaymentMethod { get; set; }
    /// <summary>Minutes paid in advance (prepaid and package sessions, including extensions).</summary>
    public int? PaidMinutes { get; set; }
}

/// <summary>
/// One stretch of play time of a session on one PC with one tariff. Pause closes a segment, resume
/// opens a new one, a transfer closes it on the old PC and opens one on the new PC. Post-paid
/// sessions are billed per segment (and per time-of-day rule inside each segment).
/// </summary>
public class SessionSegmentEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? TariffId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
}

/// <summary>
/// Append-only money ledger: every money movement is one row (no API changes or deletes rows,
/// SaveChanges refuses it, a database trigger blocks UPDATE and DELETE). A player's Balance and
/// BonusPoints always equal the sum of BalanceDelta / BonusDelta of their rows; BalanceAfter and
/// BonusAfter are the balances right after the row. DeskAmount is money taken (+) or paid out (-)
/// in cash or by card at the desk; rows with UserId null are walk-in guest payments.
/// </summary>
public class LedgerEntryEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>See Billing/LedgerTypes.</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>cash, card, balance (player balances) or none (bonus accrual, adjustment).</summary>
    public string Method { get; set; } = string.Empty;
    /// <summary>Value of the operation (always positive).</summary>
    public decimal Amount { get; set; }
    public decimal BalanceDelta { get; set; }
    public decimal BonusDelta { get; set; }
    public decimal DeskAmount { get; set; }
    public decimal? BalanceAfter { get; set; }
    public decimal? BonusAfter { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? PackageId { get; set; }
    public Guid? DeviceId { get; set; }
    public string? Description { get; set; }
    public string? Reason { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorUsername { get; set; }
    /// <summary>Shift that was open when the row was written (null: no shift open, or older rows).</summary>
    public long? ShiftId { get; set; }
    /// <summary>Bar receipt (Sales.Id) of a bar sale or refund.</summary>
    public long? SaleId { get; set; }
    /// <summary>Change of the player's debt: + post-paid time not paid, - debt paid or written off.</summary>
    public decimal DebtDelta { get; set; }
    /// <summary>Debt right after the row (null in rows of versions before the debt).</summary>
    public decimal? DebtAfter { get; set; }
}

public static class ShiftStatuses
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>
/// Work shift at the desk. Opened with a cash float; closed with the counted cash. The server computes
/// the expected cash from the ledger rows of the shift (float + every cash amount taken or paid out)
/// and stores the variance. At most one shift is open (unique index). A closed shift is final: no API
/// changes it, SaveChanges refuses it and a database trigger blocks UPDATE and DELETE.
/// </summary>
public class ShiftEntity
{
    public long Id { get; set; }
    public string Status { get; set; } = ShiftStatuses.Open;
    public DateTimeOffset OpenedUtc { get; set; }
    public Guid? OpenedById { get; set; }
    public string? OpenedByUsername { get; set; }
    public decimal OpeningFloat { get; set; }
    public string? OpenNote { get; set; }
    public DateTimeOffset? ClosedUtc { get; set; }
    public Guid? ClosedById { get; set; }
    public string? ClosedByUsername { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    /// <summary>Counted minus expected cash: below zero = cash missing.</summary>
    public decimal? Variance { get; set; }
    public string? CloseNote { get; set; }
    /// <summary>Last audit log row and its hash when the shift closed (printed on the Z-report as an anchor).</summary>
    public long? AuditHeadId { get; set; }
    public string? AuditHeadHash { get; set; }
}

/// <summary>Bar / snack product. Price changes are audited; a product with history is taken off sale, not deleted.</summary>
public class ProductEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Count the stock: sales take it, restocks add it, managers correct it (StockMovements).</summary>
    public bool TrackStock { get; set; }
    /// <summary>Cached sum of the stock movements; changed only together with a movement row (never below zero).</summary>
    public int StockQuantity { get; set; }
    /// <summary>Warn in the UI when the stock is at or below this number.</summary>
    public int? LowStockThreshold { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public static class StockMovementTypes
{
    public const string Restock = "restock";
    public const string Sale = "sale";
    public const string Refund = "refund";
    public const string Correction = "correction";
}

/// <summary>Append-only stock log: who added, sold or corrected how many of a product, and when.</summary>
public class StockMovementEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public Guid ProductId { get; set; }
    public string Type { get; set; } = string.Empty;
    public int Delta { get; set; }
    public int QuantityAfter { get; set; }
    public long? SaleId { get; set; }
    public long? ShiftId { get; set; }
    public string? Reason { get; set; }
    /// <summary>Purchase price per unit of a restock (optional, for the owner).</summary>
    public decimal? UnitCost { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorUsername { get; set; }
}

public static class SaleKinds
{
    public const string Sale = "sale";
    public const string Refund = "refund";
}

/// <summary>
/// Bar receipt (append-only). A refund is a receipt of kind "refund" that points to the sale; a unique
/// index allows one refund per sale. The money is in the ledger (bar_sale / bar_refund rows).
/// </summary>
public class SaleEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string Kind { get; set; } = SaleKinds.Sale;
    public long? RefundOfSaleId { get; set; }
    public long? ShiftId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>cash, card or balance (real balance only: bonus never pays at the bar).</summary>
    public string Method { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string? Reason { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorUsername { get; set; }
}

/// <summary>Line of a receipt; name, category and price are copied, so old receipts stay readable.</summary>
public class SaleItemEntity
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }
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

/// <summary>
/// Balance history of versions before the ledger (phase 1-2). Not written any more: the rows are
/// copied once into LedgerEntries (Billing/LedgerBackfill) and the table is kept read-only.
/// </summary>
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
    /// <summary>Hash of the previous row (64 zeros for the first row); see Auth/AuditChain.</summary>
    public string? PrevHash { get; set; }
    /// <summary>SHA-256 (hex) of PrevHash and the canonical content of this row.</summary>
    public string? Hash { get; set; }
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
    /// <summary>Cashback on top-ups: JSON list of {minAmount, percent} (Billing/BonusRules).</summary>
    public const string BonusTopUpRules = "billing.bonus_topup_rules";
    /// <summary>Written once the balances of an older version are copied into the ledger.</summary>
    public const string LedgerBackfilled = "ledger.backfilled";
    /// <summary>Written once the audit rows of an older version got their hashes (Auth/AuditChain).</summary>
    public const string AuditChainSealed = "audit.chain_sealed";
    /// <summary>Club currency, ISO code UAH (default), EUR, USD or PLN (Billing/ClubCurrency). Display only.</summary>
    public const string Currency = "club.currency";
}

/// <summary>Shifts:RequireOpenShift (default true): cash and card payments need an open shift.</summary>
public sealed class ShiftOptions
{
    public bool RequireOpenShift { get; init; } = true;

    public static ShiftOptions FromConfiguration(IConfiguration config) =>
        new() { RequireOpenShift = config.GetValue("Shifts:RequireOpenShift", true) };
}

/// <summary>A cash or card ledger row without an open shift (Shifts:RequireOpenShift). The API answers 409 shift_required.</summary>
public sealed class ShiftRequiredException : InvalidOperationException
{
    public ShiftRequiredException() : base("Open a shift first: cash and card payments need an open shift.") { }
}
