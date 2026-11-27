using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

/// <summary>A seeded PC that the demo simulator drives.</summary>
public sealed record SimulatedPc(Guid Id, string Hostname, string DeviceKey, string Ip, bool AlwaysOffline, bool? Locked, string? Mac = null);

/// <summary>PausedSessions: seeded paused sessions (the simulator resumes them after a few minutes).</summary>
public sealed record DemoSeedResult(IReadOnlyList<SimulatedPc> Pcs, IReadOnlyList<Guid>? PausedSessions = null);

/// <summary>
/// Wipes the database and seeds a demo club with typical Ukrainian prices (UAH, 2026): 3 zones
/// (Standard 12 PCs, VIP 6, Bootcamp/Pro 2), zone tariffs with weekday-day / weekend-day / evening /
/// night prices, 3 h and 5 h packs, a morning pack and a night pack per zone, cashback on top-ups,
/// 24 regular players whose balances and bonuses come from their ledger history, and 30 days of play
/// that follow the day of a real club (quiet mornings, full evenings, night packs, busier weekends),
/// with live sessions now (packages, a paused session, an open-ended one). Bar and desk
/// (DemoSeeder.Bar.cs): a bar catalog with stock, back-to-back 12 h shifts for the whole history (the
/// current one open), each closed like a real close with its audit anchor (ShiftService.ApplyClose),
/// receipts, a refund, a cash payout, deliveries, a stock correction, a player with debt, and an
/// audit log (hash chain). Games (DemoSeeder.Games.cs): a catalog of well-known games (names and
/// generated tiles only) and the games played in every session. All data is fictional. Nothing here is
/// used outside DEMO_MODE.
/// The whole club is planned in memory and written in bulk with ids in date order (about 1 s).
/// </summary>
public static partial class DemoSeeder
{
    /// <summary>AgentVersion of simulated PCs; also marks a database as a demo database.</summary>
    public const string AgentVersion = "demo-sim";

    /// <summary>This PC never comes online, so the map also shows the offline state.</summary>
    public const string OfflineHostname = "STD-12";

    /// <summary>Days of history before today: play, bar receipts and closed shifts (the analytics tab shows 30 days).</summary>
    public const int HistoryDays = 30;
    private const double ActiveShare = 0.55;
    /// <summary>Share of sessions played by registered players (the rest are walk-in guests).</summary>
    private const double PlayerShare = 0.3;

    /// <summary>Wanted occupancy per club hour on Monday to Thursday; see Wanted() for weekends and zones.</summary>
    private static readonly double[] HourlyOccupancy =
    {
        0.55, 0.45, 0.35, 0.25, 0.15, 0.08, 0.05, 0.04, 0.06, 0.10, 0.15, 0.20,
        0.26, 0.32, 0.38, 0.46, 0.56, 0.68, 0.80, 0.88, 0.92, 0.88, 0.80, 0.68,
    };

    private sealed record PackSpec(string Name, decimal Price, int? Minutes, int? WindowStart = null, int? WindowEnd = null, int Days = 127);

    private sealed record ZoneSpec(
        string Name, string Color, string HostPrefix, int Count, string TariffName,
        decimal Base, decimal WeekdayDay, decimal WeekendDay, decimal Evening, decimal Night,
        int Step, decimal MinimumCharge, double Demand, PackSpec[] Packs);

    private const int Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16, Sat = 32, Sun = 64;
    private const int MonThu = Mon | Tue | Wed | Thu, FriSun = Fri | Sat | Sun, MonFri = MonThu | Fri, All = 127;
    private const int H8 = 8 * 60, H12 = 12 * 60, H17 = 17 * 60, H22 = 22 * 60;

    private static readonly ZoneSpec[] Zones =
    {
        new("Standard", "#00f0ff", "STD", 12, "Стандарт", 60m, 45m, 55m, 70m, 40m, 1, 20m, 1.0, new[]
        {
            new PackSpec("3 години", 150m, 180),
            new PackSpec("5 годин", 230m, 300),
            new PackSpec("Ранковий (3 год, до 12:00)", 99m, 180, H8, H12, MonFri),
            new PackSpec("Нічний 22:00–08:00", 250m, null, H22, H8),
        }),
        new("VIP", "#bf5af2", "VIP", 6, "VIP", 90m, 70m, 80m, 100m, 60m, 5, 30m, 0.9, new[]
        {
            new PackSpec("3 години VIP", 240m, 180),
            new PackSpec("5 годин VIP", 370m, 300),
            new PackSpec("Нічний VIP 22:00–08:00", 380m, null, H22, H8),
        }),
        new("Bootcamp / Pro", "#ffe019", "PRO", 2, "Bootcamp", 120m, 95m, 110m, 140m, 80m, 15, 40m, 0.75, new[]
        {
            new PackSpec("3 години Bootcamp", 330m, 180),
            new PackSpec("5 годин Bootcamp", 520m, 300),
            new PackSpec("Нічний Bootcamp 22:00–08:00", 500m, null, H22, H8),
        }),
    };

    /// <summary>Demo loyalty tiers: real money spent in the last 30 days (the seeded regulars spread over them).</summary>
    public static readonly LoyaltySettings LoyaltyTiers = new(30, new[]
    {
        new LoyaltyTier("Bronze", 0m, 0m, 0m),
        new LoyaltyTier("Silver", 2000m, 2m, 5m),
        new LoyaltyTier("Gold", 6000m, 5m, 10m),
    });

    public static readonly IReadOnlyList<BonusTier> BonusTiers = new[]
    {
        new BonusTier(200m, 5m), new BonusTier(500m, 10m), new BonusTier(1000m, 15m)
    };

    private static readonly (string Username, string DisplayName)[] Players =
    {
        (DemoOptions.PlayerLogin, "Demo Player"),
        ("nightowl", "Night Owl"), ("vika_gg", "Віка"), ("taras_ua", "Тарас"), ("frostbyte", "Frostbyte"),
        ("olena_k", "Олена К."), ("maks_pro", "Макс"), ("headshot", "Headshot Hero"), ("dark_knight", "Dark Knight"),
        ("sasha_cs", "Саша"), ("ira_heals", "Іра"), ("bohdan99", "Богдан"), ("kyiv_sniper", "Kyiv Sniper"),
        ("lviv_lion", "Lviv Lion"), ("marta", "Марта"), ("dmytro_dota", "Дмитро"), ("noob_master", "NoobMaster"),
        ("andrii_v", "Андрій В."), ("yulia_gg", "Юля"), ("rostyk", "Ростик"), ("kozak_pro", "Kozak Pro"),
        ("ninja_ua", "Ninja UA"), ("oksana_m", "Оксана М."), ("pixel", "Pixel"),
    };

    /// <summary>Tables with many rows: PostgreSQL empties them with one TRUNCATE.</summary>
    private static readonly string[] BigTables =
        { "Commands", "Heartbeats", "Transactions", "GamePlays", "SessionSegments", "Sessions", "SaleItems", "Sales", "StockMovements", "Shifts", "LedgerEntries", "AuditLog", "Reservations",
          "BarOrderItems", "BarOrders", "EventMatches", "EventPlayers", "EventTeams", "EventDevices", "Events", "GameAccountLeases", "GameAccounts" };

    /// <summary>Tables of the bulk insert whose foreign keys PostgreSQL checks once at the end.</summary>
    private static readonly string[] BulkTables = { "Sessions", "SessionSegments", "GamePlays", "Sales", "SaleItems", "StockMovements", "LedgerEntries" };

    private sealed record ForeignKeyDefinition(string Table, string Name, string Definition);

    /// <summary>Tables with ids from a sequence that the seed fills with explicit ids in date order.</summary>
    private static readonly string[] SequenceTables = { "Shifts", "Sales", "SaleItems", "StockMovements", "LedgerEntries", "AuditLog", "Reservations", "BarOrderItems", "BarOrders",
        "Events", "EventTeams", "EventPlayers", "EventMatches", "GameAccountLeases" };

    /// <summary>
    /// True when the database is empty or was seeded by demo mode. Demo mode refuses to
    /// start on any other database, so DEMO_MODE=true can never wipe a real club.
    /// </summary>
    public static async Task<bool> IsDemoOrEmptyAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (await db.Devices.AnyAsync(d => d.AgentVersion == AgentVersion, ct)) return true;
        return !await db.Devices.AnyAsync(ct)
               && !await db.Users.AnyAsync(ct)
               && !await db.Products.AnyAsync(ct)
               && !await db.Shifts.AnyAsync(ct)
               && !await db.Zones.AnyAsync(ct)
               && !await db.Tariffs.AnyAsync(ct)
               && !await db.Packages.AnyAsync(ct)
               && !await db.Games.AnyAsync(ct)
               && !await db.Sessions.AnyAsync(ct)
               && !await db.Ledger.AnyAsync(ct)
               && !await db.Reservations.AnyAsync(ct)
               && !await db.BarOrders.AnyAsync(ct)
               && !await db.Events.AnyAsync(ct)
               && !await db.GameAccounts.AnyAsync(ct)
               && !await db.StaffUsers.AnyAsync(ct);
    }

    public static Task<DemoSeedResult> ResetAsync(CmsDbContext db, DateTimeOffset now, Random rng, CancellationToken ct = default) =>
        ResetAsync(db, now, rng, ClubTimeZone.Default, ct);

    /// <param name="vault">The vault of the server for the fake club accounts (null: keys in memory only).</param>
    public static async Task<DemoSeedResult> ResetAsync(CmsDbContext db, DateTimeOffset now, Random rng, TimeZoneInfo zone, CancellationToken ct = default,
        Cms.Server.Accounts.GameAccountVault? vault = null)
    {
        // Plan the whole club in memory first: ids of shifts, receipts, the stock log, the ledger and the
        // audit log are given in date order, and every closed shift gets its anchor in the audit chain
        var seed = new Seeding(now, rng, zone);
        var result = seed.Run();
        var audit = seed.BuildAuditChain();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        List<ForeignKeyDefinition> foreignKeys = new();
        if (db.Database.IsNpgsql())
        {
            // The big tables in one TRUNCATE (row triggers of the append-only tables do not fire), the small ones in foreign key order
            await db.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE {string.Join(", ", BigTables.Select(t => $"\"{t}\""))} RESTART IDENTITY", ct);
            await db.GameZones.ExecuteDeleteAsync(ct);
            await db.Games.ExecuteDeleteAsync(ct);
            await db.Packages.ExecuteDeleteAsync(ct);
            await db.TariffRules.ExecuteDeleteAsync(ct);
            await db.Tariffs.ExecuteDeleteAsync(ct);
            await db.Devices.ExecuteDeleteAsync(ct);
            await db.Users.ExecuteDeleteAsync(ct);
            await db.Zones.ExecuteDeleteAsync(ct);
            await db.Products.ExecuteDeleteAsync(ct);
            await db.StaffUsers.ExecuteDeleteAsync(ct);
            await db.ClubSettings.ExecuteDeleteAsync(ct);
            await db.ClubLogos.ExecuteDeleteAsync(ct);
            // The foreign keys of the bulk tables are checked once, when they are added back after the
            // insert, instead of once per row (PostgreSQL: DDL is part of the transaction)
            foreignKeys = await db.Database.SqlQueryRaw<ForeignKeyDefinition>(
                "SELECT c.conrelid::regclass::text AS \"Table\", c.conname AS \"Name\", pg_get_constraintdef(c.oid) AS \"Definition\" " +
                "FROM pg_constraint c WHERE c.contype = 'f' AND c.conrelid::regclass::text IN (" +
                string.Join(", ", BulkTables.Select(t => $"'\"{t}\"'")) + ")").ToListAsync(ct);
            foreach (var fk in foreignKeys)
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {fk.Table} DROP CONSTRAINT \"{fk.Name}\"", ct);
        }
        else
        {
            // Children first (foreign key order); the append-only tables are cleared the demo way
            await db.GameAccountLeases.ExecuteDeleteAsync(ct);
            await db.GameAccounts.ExecuteDeleteAsync(ct);
            await db.EventMatches.ExecuteDeleteAsync(ct);
            await db.EventPlayers.ExecuteDeleteAsync(ct);
            await db.EventTeams.ExecuteDeleteAsync(ct);
            await db.EventDevices.ExecuteDeleteAsync(ct);
            await db.Events.ExecuteDeleteAsync(ct);
            await db.Reservations.ExecuteDeleteAsync(ct);
            await db.BarOrderItems.ExecuteDeleteAsync(ct);
            await db.BarOrders.ExecuteDeleteAsync(ct);
            await db.Commands.ExecuteDeleteAsync(ct);
            await db.Heartbeats.ExecuteDeleteAsync(ct);
            await LedgerGuard.ClearForDemoResetAsync(db, ct);
            await PosGuard.ClearForDemoResetAsync(db, ct);
            await db.Transactions.ExecuteDeleteAsync(ct);
            await db.GamePlays.ExecuteDeleteAsync(ct);
            await db.GameZones.ExecuteDeleteAsync(ct);
            await db.Games.ExecuteDeleteAsync(ct);
            await db.SessionSegments.ExecuteDeleteAsync(ct);
            await db.Sessions.ExecuteDeleteAsync(ct);
            await db.Devices.ExecuteDeleteAsync(ct);
            await db.Packages.ExecuteDeleteAsync(ct);
            await db.TariffRules.ExecuteDeleteAsync(ct);
            await db.Tariffs.ExecuteDeleteAsync(ct);
            await db.Users.ExecuteDeleteAsync(ct);
            await db.Zones.ExecuteDeleteAsync(ct);
            await AuditLogGuard.ClearForDemoResetAsync(db, ct);
            await db.StaffUsers.ExecuteDeleteAsync(ct);
            await db.ClubSettings.ExecuteDeleteAsync(ct);
            await db.ClubLogos.ExecuteDeleteAsync(ct);
        }
        db.ChangeTracker.Clear();
        if (db.Database.IsSqlite() &&
            await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE name = 'sqlite_sequence'").SingleAsync(ct) > 0)
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM sqlite_sequence WHERE name IN ({string.Join(", ", SequenceTables.Select(t => $"'{t}'"))})", ct);

        // Bulk insert in foreign key order. The rows are complete (balances after each ledger row, the
        // shift of each row, hashes of the audit rows), so they skip the change tracker (Data/BulkInsert)
        foreach (var rows in new IEnumerable<object>[] { seed.Club, seed.EventRows, seed.Sessions, seed.Segments, seed.Plays, seed.Shifts, seed.Sales,
                     seed.SaleItems.SelectMany(x => x.Items), seed.Movements, seed.LedgerRows, audit })
            await BulkInsert.InsertAsync(db, rows, ct);

        foreach (var fk in foreignKeys)
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {fk.Table} ADD CONSTRAINT \"{fk.Name}\" {fk.Definition}", ct);

        await BonusRules.SaveAsync(db, BonusTiers, "demo", now, ct);
        await Loyalty.SaveAsync(db, LoyaltyTiers, "demo", now, ct);
        db.ClubSettings.Add(new ClubSettingEntity { Key = ClubSettingKeys.LedgerBackfilled, Value = "demo", UpdatedUtc = now, UpdatedBy = "demo" });
        db.ClubSettings.Add(new ClubSettingEntity { Key = ClubSettingKeys.AuditChainSealed, Value = "demo", UpdatedUtc = now, UpdatedBy = "demo" });
        await db.SaveChangesAsync(ct);

        // PostgreSQL: the next generated id follows the seeded ids (SQLite does this by itself)
        if (db.Database.IsNpgsql())
        {
            foreach (var table in SequenceTables)
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT setval(pg_get_serial_sequence('\"{table}\"', 'Id'), COALESCE((SELECT MAX(\"Id\") FROM \"{table}\"), 0) + 1, false)", ct);
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        // Fake club accounts (random secrets made now) and the leases of the PCs that play a game that needs one
        await DemoAccounts.SeedAsync(db, vault ?? Cms.Server.Accounts.GameAccountVault.Ephemeral(), now, zone, ct);
        db.ChangeTracker.Clear();

        // An event in two days, through the real service (entry fees in the ledger, audit rows), then the reservations
        // of today and tomorrow (deposits in the ledger, audit rows)
        await SeedUpcomingEventAsync(db, now, zone, ct);
        db.ChangeTracker.Clear();
        await DemoReservations.SeedAsync(db, now, zone, rng, ct);
        db.ChangeTracker.Clear();
        return result;
    }

    private static string? _demoHash;

    private static T Pick<T>(Random rng, params T[] items) => items[rng.Next(items.Length)];

    /// <summary>Wanted occupancy at a club time: busier Friday and Saturday nights and weekend days, quieter weekday days.</summary>
    private static double Wanted(DateTimeOffset local, double zoneDemand)
    {
        var p = HourlyOccupancy[local.Hour];
        var day = local.DayOfWeek;
        if (day is DayOfWeek.Friday or DayOfWeek.Saturday && (local.Hour >= 17 || local.Hour < 3)) p = Math.Min(0.96, p * 1.12);
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday && local.Hour is >= 10 and < 17) p = Math.Min(0.9, p * 1.5);
        if (day == DayOfWeek.Sunday && local.Hour >= 21) p *= 0.8;
        if (day is >= DayOfWeek.Monday and <= DayOfWeek.Thursday && local.Hour is >= 9 and < 16) p *= 0.85;
        return Math.Min(0.96, p * zoneDemand);
    }

    private sealed record PcInfo(DeviceEntity Device, TariffEntity Tariff, TariffPricing Pricing, List<PackageEntity> Packs, double Demand);

    /// <summary>A history session before it gets a player: prepaid minutes, a package (maybe left early) or post-paid time.</summary>
    private sealed record PlannedSession(PcInfo Pc, DateTimeOffset Start, DateTimeOffset End, int? Minutes, PackageEntity? Package, PackageTerms? Terms, bool PostPaid);

    /// <summary>One seeding run (keeps the generated data together). Pure in memory: ResetAsync writes the result.</summary>
    private sealed partial class Seeding
    {
        private readonly DateTimeOffset _now;
        private readonly Random _rng;
        private readonly TimeZoneInfo _zone;
        private readonly List<PcInfo> _pcs = new();
        private readonly Dictionary<Guid, (decimal Balance, decimal Bonus)> _wallets = new();
        private readonly Dictionary<Guid, decimal> _debts = new();
        private readonly List<(DateTimeOffset At, int Order, Action Apply)> _playerEvents = new();
        /// <summary>Fixed sessions per PC (live, debtor, free time): the history plays around them.</summary>
        private readonly Dictionary<Guid, List<(DateTimeOffset Start, DateTimeOffset End)>> _reserved = new();
        /// <summary>When each player plays, so that nobody plays on two PCs at once.</summary>
        private readonly Dictionary<Guid, List<(DateTimeOffset Start, DateTimeOffset End)>> _playing = new();
        private List<UserEntity> _users = new();
        private List<UserEntity> _regulars = new();
        /// <summary>The salted hash of the public demo password, made once per process (PBKDF2 is slow on purpose).</summary>
        private static string DemoPasswordHash => _demoHash ??= Passwords.Hash(DemoOptions.StaffPassword);

        /// <summary>Zones, tariffs, rules, packages, PCs, staff, players and products (written first).</summary>
        public List<object> Club { get; } = new();
        public List<SessionEntity> Sessions { get; } = new();
        public List<SessionSegmentEntity> Segments { get; } = new();
        public List<LedgerEntryEntity> LedgerRows { get; } = new();

        public Seeding(DateTimeOffset now, Random rng, TimeZoneInfo zone)
        {
            _now = now;
            _rng = rng;
            _zone = zone;
        }

        public DemoSeedResult Run()
        {
            var pcs = SeedClub();
            SeedGames();
            SeedStaff();
            SeedPlayers();
            PlanWindows();
            var paused = SeedLiveSessions(pcs);
            SeedDebtor();
            SeedFreeTime();
            SeedFinishedEvent();
            SeedHistory();
            SeedPlays();
            SeedBar();
            ApplyPlayerEvents();
            PlanShifts();
            AssignIds();
            return new DemoSeedResult(pcs, paused);
        }

        private List<SimulatedPc> SeedClub()
        {
            var pcs = new List<SimulatedPc>();
            var ipSuffix = 101;
            var row = 0;
            for (var z = 0; z < Zones.Length; z++)
            {
                var spec = Zones[z];
                var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = spec.Name, Color = spec.Color, SortOrder = z };
                _zoneIds.Add(zone.Id);
                var tariff = new TariffEntity
                {
                    Id = Guid.NewGuid(), Name = spec.TariffName, PricePerHour = spec.Base, IsDefault = z == 0, ZoneId = zone.Id,
                    BillingStepMinutes = spec.Step, MinimumCharge = spec.MinimumCharge
                };
                var rules = new List<TariffRuleEntity>
                {
                    Rule(tariff, 0, "Будні вдень (Пн–Чт 08–17)", MonThu, H8, H17, spec.WeekdayDay),
                    Rule(tariff, 1, "Вихідні вдень (Пт–Нд 08–17)", FriSun, H8, H17, spec.WeekendDay),
                    Rule(tariff, 2, "Вечір (17–22)", All, H17, H22, spec.Evening),
                    Rule(tariff, 3, "Ніч (22–08)", All, H22, H8, spec.Night),
                };
                Club.Add(zone);
                Club.Add(tariff);
                Club.AddRange(rules);
                var pricing = TariffPricing.From(tariff, rules);

                var packs = spec.Packs.Select((p, i) => new PackageEntity
                {
                    Id = Guid.NewGuid(), Name = p.Name, ZoneId = zone.Id, Price = p.Price, DurationMinutes = p.Minutes,
                    WindowStartMinute = p.WindowStart, WindowEndMinute = p.WindowEnd, DaysMask = p.Days, IsActive = true, SortOrder = i
                }).ToList();
                Club.AddRange(packs);

                if (z == 0)
                {
                    // A second tariff of the Standard zone that the cashier can choose
                    var student = new TariffEntity
                    {
                        Id = Guid.NewGuid(), Name = "Студентський", PricePerHour = 60m, ZoneId = zone.Id, BillingStepMinutes = 1, MinimumCharge = 20m
                    };
                    Club.Add(student);
                    Club.Add(Rule(student, 0, "Студентам Пн–Пт 08–17", MonFri, H8, H17, 35m));
                }

                for (var i = 1; i <= spec.Count; i++)
                {
                    var hostname = $"{spec.HostPrefix}-{i:00}";
                    var offline = hostname == OfflineHostname;
                    var ip = $"10.3.3.{ipSuffix++}";
                    var deviceKey = $"demo-{Guid.NewGuid():N}"; // the database stores only its hash
                    var mac = $"02:CC:4D:53:{z:X2}:{i:X2}";     // locally administered: no real card has it
                    var device = new DeviceEntity
                    {
                        Id = Guid.NewGuid(),
                        Hostname = hostname,
                        OsVersion = "Microsoft Windows NT 10.0.22631.0",
                        AgentVersion = AgentVersion,
                        DeviceKey = DeviceKeys.Hash(deviceKey),
                        Status = DeviceStatuses.Approved,
                        RegisteredUtc = _now.AddDays(-HistoryDays - 10),
                        LastSeenUtc = offline ? _now.AddHours(-3).AddMinutes(-12) : _now,
                        LastIp = ip,
                        ZoneId = zone.Id,
                        PositionX = (i - 1) % 6,
                        PositionY = row + (i - 1) / 6,
                        MacAddress = mac
                    };
                    Club.Add(device);
                    _pcs.Add(new PcInfo(device, tariff, pricing, packs, spec.Demand));
                    pcs.Add(new SimulatedPc(device.Id, hostname, deviceKey, ip, offline, null, mac));
                }
                row += (spec.Count + 5) / 6 + 1;
            }
            return pcs;
        }

        private static TariffRuleEntity Rule(TariffEntity tariff, int order, string name, int days, int start, int end, decimal price) => new()
        {
            Id = Guid.NewGuid(), TariffId = tariff.Id, Name = name, DaysMask = days, StartMinute = start, EndMinute = end,
            PricePerHour = price, SortOrder = order
        };

        private void SeedStaff()
        {
            // Demo staff accounts (public demo password, demo databases only)
            var staffHash = DemoPasswordHash;
            foreach (var (username, displayName, role) in DemoOptions.StaffAccounts)
            {
                var staff = new StaffUserEntity
                {
                    Id = Guid.NewGuid(), Username = username, DisplayName = displayName,
                    PasswordHash = staffHash, Role = role, IsActive = true, CreatedUtc = _now
                };
                Club.Add(staff);
                _staff[role] = staff;
            }
        }

        private DateTimeOffset HistoryStart => LocalTime(Today.AddDays(-HistoryDays), 0);

        private void SeedPlayers()
        {
            // Password "demo" for all of them; demo data only
            var passwordHash = DemoOptions.PlayerPassword == DemoOptions.StaffPassword ? DemoPasswordHash : Passwords.Hash(DemoOptions.PlayerPassword);
            _users = Players.Select(p => new UserEntity
            {
                Id = Guid.NewGuid(),
                Username = p.Username,
                DisplayName = p.DisplayName,
                PasswordHash = passwordHash,
                CreatedUtc = _now.AddDays(-_rng.Next(HistoryDays + 5, 150)).AddMinutes(-_rng.Next(0, 1440))
            }).ToList();
            _regulars = _users.ToList();
            Club.AddRange(_users);
            foreach (var user in _users) _wallets[user.Id] = (0m, 0m);

            // Top-ups in cash or by card at the start of the history (cashback by the demo tiers)
            foreach (var user in _users)
            {
                var count = _rng.Next(1, 4);
                for (var i = 0; i < count; i++)
                {
                    var at = HistoryStart.AddMinutes(_rng.Next(60, 4 * 1440));
                    var amount = Pick(_rng, 100m, 200m, 300m, 500m, 500m, 1000m);
                    var method = _rng.NextDouble() < 0.6 ? PaymentMethods.Cash : PaymentMethods.Card;
                    _playerEvents.Add((at, 0, () => TopUp(user, amount, method, at)));
                }
            }

            // Recent top-ups (yesterday, today) with cashback, so bonus balances are visible
            foreach (var user in _users.Where(_ => _rng.NextDouble() < 0.6))
            {
                var at = _now.AddHours(-_rng.Next(6, 30)).AddMinutes(-_rng.Next(0, 60));
                var amount = Pick(_rng, 200m, 300m, 500m, 1000m);
                var method = _rng.NextDouble() < 0.5 ? PaymentMethods.Cash : PaymentMethods.Card;
                _playerEvents.Add((at, 0, () => TopUp(user, amount, method, at)));
            }

            // One refund and one correction with a reason (refund / adjustment history)
            var olena = _users.Single(u => u.Username == "olena_k");
            var refundAt = _now.AddDays(-2).AddHours(-3);
            _playerEvents.Add((refundAt, 2, () => Refund(olena, 100m, "Не змогла прийти на турнір, повернення частини поповнення", refundAt)));
            var taras = _users.Single(u => u.Username == "taras_ua");
            var adjustAt = _now.AddDays(-1).AddHours(-5);
            _playerEvents.Add((adjustAt, 2, () => Adjust(taras, 50m, "Компенсація: збій ПК STD-04 під час гри", adjustAt)));
        }

        /// <summary>
        /// How often each regular comes, by the position in the player list: some play every evening, some now and then,
        /// so the players spread over the loyalty tiers (Bronze, Silver, Gold of the demo).
        /// </summary>
        private static readonly double[] Activity = { 0.1, 0.15, 0.5, 0.6, 1.6, 2.5 };

        private UserEntity PickWeighted(IReadOnlyList<UserEntity> users)
        {
            double Weight(UserEntity u) => Activity[Math.Max(0, Array.FindIndex(Players, p => p.Username == u.Username)) % Activity.Length];
            var roll = _rng.NextDouble() * users.Sum(Weight);
            foreach (var u in users)
            {
                roll -= Weight(u);
                if (roll <= 0) return u;
            }
            return users[^1];
        }

        private void Reserve(PcInfo pc, DateTimeOffset start, DateTimeOffset end)
        {
            if (!_reserved.TryGetValue(pc.Device.Id, out var list)) _reserved[pc.Device.Id] = list = new();
            list.Add((start, end));
        }

        private void Playing(UserEntity user, DateTimeOffset start, DateTimeOffset end)
        {
            if (!_playing.TryGetValue(user.Id, out var list)) _playing[user.Id] = list = new();
            list.Add((start, end));
        }

        private bool IsFree(UserEntity user, DateTimeOffset start, DateTimeOffset end) =>
            !_playing.TryGetValue(user.Id, out var list) || list.All(p => p.End.AddMinutes(10) <= start || p.Start >= end.AddMinutes(10));

        /// <summary>The end of a fixed session of this PC that overlaps [start, end), or null.</summary>
        private DateTimeOffset? Blocked(PcInfo pc, DateTimeOffset start, DateTimeOffset end) =>
            _reserved.TryGetValue(pc.Device.Id, out var list)
                ? list.Where(r => r.Start < end.AddMinutes(3) && r.End > start).Select(r => (DateTimeOffset?)r.End).Max()
                : null;

        /// <summary>
        /// 30 days of play per PC that follow the wanted occupancy of each club hour: when a PC is free,
        /// a visitor sits down with the chance 15 p / (L (1 - p)) per 15 minutes (p = wanted occupancy,
        /// L = mean session length), so the PC is busy about p of the time. About one session in three is a
        /// registered player (never on two PCs at once), the rest are walk-in guests.
        /// </summary>
        private void SeedHistory()
        {
            var plans = new List<PlannedSession>();
            foreach (var pc in _pcs)
            {
                var limit = pc.Device.Hostname == OfflineHostname ? pc.Device.LastSeenUtc!.Value.AddMinutes(-20) : _now.AddMinutes(-_rng.Next(3, 20));
                if (_reserved.TryGetValue(pc.Device.Id, out var fixedSessions))
                {
                    // The live session of this PC is the last one
                    var live = fixedSessions.Where(r => r.End > _now).Select(r => (DateTimeOffset?)r.Start).Min();
                    if (live is { } liveStart && liveStart.AddMinutes(-5) < limit) limit = liveStart.AddMinutes(-_rng.Next(5, 25));
                }
                var t = HistoryStart.AddMinutes(_rng.Next(0, 30));
                while (t < limit)
                {
                    var local = TimeZoneInfo.ConvertTime(t, _zone);
                    var p = Wanted(local, pc.Demand);
                    var mean = local.Hour is >= 7 and < 14 ? 95.0 : 150.0;
                    if (_rng.NextDouble() >= Math.Min(0.97, 15 * p / (mean * (1 - p))))
                    {
                        t = t.AddMinutes(15);
                        continue;
                    }
                    var plan = PlanSession(pc, t, local);
                    if (plan.End > limit) break;
                    if (Blocked(pc, plan.Start, plan.End) is { } after)
                    {
                        t = after.AddMinutes(_rng.Next(3, 20));
                        continue;
                    }
                    plans.Add(plan);
                    t = plan.End.AddMinutes(_rng.Next(3, 21));
                }
            }

            foreach (var plan in plans.OrderBy(p => p.Start))
            {
                UserEntity? user = null;
                if (_rng.NextDouble() < PlayerShare)
                {
                    var free = _regulars.Where(u => IsFree(u, plan.Start, plan.End)).ToList();
                    if (free.Count > 0)
                    {
                        user = PickWeighted(free);
                        Playing(user, plan.Start, plan.End);
                    }
                }
                AddPlanned(plan, user);
            }
        }

        private PlannedSession PlanSession(PcInfo pc, DateTimeOffset t, DateTimeOffset local)
        {
            // The night pack from 22:00 to 01:00; 60 % of the night players leave before the 08:00 cut-off
            if ((local.Hour >= 21 || local.Hour < 1) && _rng.NextDouble() < 0.3)
            {
                var night = pc.Packs.FirstOrDefault(p => p.DurationMinutes == null);
                var offer = night == null ? null : PackageWindows.Evaluate(night, t, _zone);
                if (offer is { Available: true, Terms: { } terms })
                {
                    var end = _rng.NextDouble() < 0.4 ? terms.EndUtc : t.AddMinutes(_rng.Next(Math.Min(180, terms.Minutes), terms.Minutes + 1));
                    return new PlannedSession(pc, t, end, terms.Minutes, night, terms, false);
                }
            }
            if (_rng.NextDouble() < 0.2)
            {
                var packs = pc.Packs.Where(p => p.DurationMinutes != null)
                    .Select(p => (Package: p, Offer: PackageWindows.Evaluate(p, t, _zone))).Where(x => x.Offer.Available).ToList();
                if (packs.Count > 0)
                {
                    var (package, offer) = packs[_rng.Next(packs.Count)];
                    return new PlannedSession(pc, t, offer.Terms!.EndUtc, offer.Terms.Minutes, package, offer.Terms, false);
                }
            }
            if (_rng.NextDouble() < 0.1)
            {
                var played = Pick(_rng, 45, 60, 75, 90, 120, 150, 180, 240) + _rng.Next(-7, 8);
                return new PlannedSession(pc, t, t.AddMinutes(played).AddSeconds(_rng.Next(0, 60)), null, null, null, true);
            }
            var minutes = local.Hour is >= 7 and < 14 ? Pick(_rng, 60, 60, 90, 120, 120, 120) : Pick(_rng, 60, 90, 120, 120, 150, 180, 180, 240);
            return new PlannedSession(pc, t, t.AddMinutes(minutes), minutes, null, null, false);
        }

        private void AddPlanned(PlannedSession plan, UserEntity? user)
        {
            var pc = plan.Pc;
            if (plan.Package is { } package)
            {
                var s = AddSession(pc, plan.Start, plan.End, package.Price, true, package, plan.Terms!.Minutes, user, "ended", cutoffUtc: plan.Terms.CutoffUtc);
                Pay(s, user, package.Price, LedgerTypes.PackagePurchase, $"Пакет «{package.Name}» на {pc.Device.Hostname} — {MoneyText.Of(package.Price)} ₴");
            }
            else if (plan.PostPaid)
            {
                // Post-paid: billed at the end per rule, rounded to the tariff step
                var cost = TariffCalculator.QuotePlayed(new[] { (pc.Pricing, plan.Start, plan.End) }, pc.Pricing, _zone).Total;
                var s = AddSession(pc, plan.Start, plan.End, cost, false, null, null, user, "ended");
                Pay(s, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} ({(int)Math.Round((plan.End - plan.Start).TotalMinutes)} хв) — {MoneyText.Of(cost)} ₴");
            }
            else
            {
                var cost = TariffCalculator.QuoteFixed(pc.Pricing, plan.Start, plan.Minutes!.Value, _zone).Total;
                var s = AddSession(pc, plan.Start, plan.End, cost, true, null, plan.Minutes, user, "ended");
                Pay(s, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} ({plan.Minutes} хв, {pc.Tariff.Name}) — {MoneyText.Of(cost)} ₴");
            }
        }

        /// <summary>Live sessions on about half of the online PCs; the rest wait at the lock screen.</summary>
        private List<Guid> SeedLiveSessions(List<SimulatedPc> pcs)
        {
            var paused = new List<Guid>();
            var online = _pcs.Where(p => p.Device.Hostname != OfflineHostname).OrderBy(_ => _rng.Next()).ToList();
            var activeCount = (int)Math.Round(online.Count * ActiveShare);
            for (var i = 0; i < online.Count; i++)
            {
                var pc = online[i];
                var index = pcs.FindIndex(p => p.Id == pc.Device.Id);
                if (i >= activeCount)
                {
                    pcs[index] = pcs[index] with { Locked = true };
                    continue;
                }

                var candidates = _regulars.Where(u => IsFree(u, _now.AddHours(-6), _now.AddHours(6))).ToList();
                var user = _rng.NextDouble() < 0.4 && candidates.Count > 0 ? candidates[_rng.Next(candidates.Count)] : null;
                var locked = false;
                SessionEntity session;
                if (i == activeCount - 1)
                {
                    // One open-ended (post-paid) session, billed when it is ended
                    session = AddSession(pc, _now.AddMinutes(-_rng.Next(10, 200)), null, 0m, false, null, null, user, "active");
                }
                else if (i == activeCount - 2)
                {
                    // One paused session (the player went out for a moment); the simulator resumes it
                    var start = _now.AddMinutes(-_rng.Next(30, 50));
                    var cost = TariffCalculator.QuoteFixed(pc.Pricing, start, 120, _zone).Total;
                    session = AddSession(pc, start, start.AddMinutes(120), cost, true, null, 120, user, "active", pausedUtc: _now.AddMinutes(-4));
                    Pay(session, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} (120 хв, {pc.Tariff.Name}) — {MoneyText.Of(cost)} ₴");
                    paused.Add(session.Id);
                    locked = true;
                }
                else if (i % 3 == 2 && TryPackage(pc, _now.AddMinutes(-_rng.Next(10, 90)), out var package, out var terms) && terms.EndUtc > _now.AddMinutes(5))
                {
                    session = AddSession(pc, terms.StartUtc, terms.EndUtc, package.Price, true, package, terms.Minutes, user, "active", cutoffUtc: terms.CutoffUtc);
                    Pay(session, user, package.Price, LedgerTypes.PackagePurchase, $"Пакет «{package.Name}» на {pc.Device.Hostname} — {MoneyText.Of(package.Price)} ₴");
                }
                else
                {
                    var duration = Pick(_rng, 60, 60, 120, 120, 180, 300);
                    // The first two expire within minutes, so auto-lock is visible soon after a reset
                    var remaining = i < 2 ? _rng.Next(2, 6) : _rng.Next(8, duration - 4);
                    var start = _now.AddMinutes(remaining - duration);
                    var cost = TariffCalculator.QuoteFixed(pc.Pricing, start, duration, _zone).Total;
                    session = AddSession(pc, start, _now.AddMinutes(remaining), cost, true, null, duration, user, "active");
                    Pay(session, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} ({duration} хв, {pc.Tariff.Name}) — {MoneyText.Of(cost)} ₴");
                }
                Reserve(pc, session.StartUtc, _now.AddDays(1));
                if (user != null) Playing(user, session.StartUtc, _now.AddDays(1));
                pcs[index] = pcs[index] with { Locked = locked };
            }
            return paused;
        }

        private bool TryPackage(PcInfo pc, DateTimeOffset start, out PackageEntity package, out PackageTerms terms)
        {
            // A night pack at night, else a 3 h / 5 h / morning pack, whichever is on sale at that time
            var available = pc.Packs
                .Select(p => (Package: p, Availability: PackageWindows.Evaluate(p, start, _zone)))
                .Where(x => x.Availability.Available).ToList();
            var night = available.Where(x => x.Package.DurationMinutes == null).ToList();
            var choice = night.Count > 0 && _rng.NextDouble() < 0.7 ? night : available;
            if (choice.Count == 0)
            {
                package = null!;
                terms = null!;
                return false;
            }
            var pick = choice[_rng.Next(choice.Count)];
            package = pick.Package;
            terms = pick.Availability.Terms!;
            return true;
        }

        private SessionEntity AddSession(PcInfo pc, DateTimeOffset start, DateTimeOffset? end, decimal cost, bool prepaid, PackageEntity? package,
            int? paidMinutes, UserEntity? user, string status, DateTimeOffset? pausedUtc = null, DateTimeOffset? cutoffUtc = null)
        {
            var session = new SessionEntity
            {
                Id = Guid.NewGuid(), DeviceId = pc.Device.Id, TariffId = pc.Tariff.Id, UserId = user?.Id,
                StartUtc = start, EndUtc = end, Status = status, TotalCost = cost, IsPrepaid = prepaid,
                PackageId = package?.Id, PaidMinutes = paidMinutes, PausedUtc = pausedUtc,
                CutoffUtc = cutoffUtc
            };
            Sessions.Add(session);
            var segmentEnd = pausedUtc ?? (status == "ended" ? end : null);
            Segments.Add(new SessionSegmentEntity
            {
                Id = Guid.NewGuid(), SessionId = session.Id, DeviceId = pc.Device.Id, TariffId = pc.Tariff.Id, StartUtc = start, EndUtc = segmentEnd
            });
            return session;
        }

        /// <summary>A player pays from the balances (bonus first; topped up first when too low), a guest at the desk.</summary>
        private void Pay(SessionEntity session, UserEntity? user, decimal amount, string type, string description)
        {
            if (amount <= 0) return;
            var at = session.StartUtc;
            if (user == null)
            {
                var method = _rng.NextDouble() < 0.7 ? PaymentMethods.Cash : PaymentMethods.Card;
                LedgerRows.Add(new LedgerEntryEntity
                {
                    CreatedUtc = session.IsPrepaid ? at : session.EndUtc ?? at, Type = type, Method = method, Amount = amount,
                    DeskAmount = amount, SessionId = session.Id, PackageId = session.PackageId, DeviceId = session.DeviceId,
                    Description = description
                });
                session.PaymentMethod = method;
                return;
            }
            session.PaymentMethod = PaymentMethods.Balance;
            var when = session.IsPrepaid ? at : session.EndUtc ?? at;
            var useBonus = _rng.NextDouble() < 0.5; // some players keep their bonus for later
            _playerEvents.Add((when, 1, () =>
            {
                var (balance, bonus) = _wallets[user.Id];
                var usable = useBonus ? bonus : 0m;
                if (balance + usable < amount)
                    TopUp(user, Math.Ceiling((amount - balance - usable) / 100m) * 100m + 200m, PaymentMethods.Cash, when.AddMilliseconds(-10));
                (balance, bonus) = _wallets[user.Id];
                var fromBonus = useBonus ? Math.Min(bonus, amount) : 0m;
                var fromBalance = amount - fromBonus;
                Post(user, when, type, PaymentMethods.Balance, amount, -fromBalance, -fromBonus, 0m, description, session);
            }));
        }

        private void TopUp(UserEntity user, decimal amount, string method, DateTimeOffset at)
        {
            Post(user, at, LedgerTypes.TopUp, method, amount, amount, 0m, amount, $"Поповнення балансу +{MoneyText.Of(amount)} ₴");
            var tier = BonusRules.TierFor(BonusTiers, amount);
            var bonus = BonusRules.CashbackFor(BonusTiers, amount);
            if (bonus > 0)
                Post(user, at.AddMilliseconds(1), LedgerTypes.BonusAccrual, PaymentMethods.None, bonus, 0m, bonus, 0m,
                    $"Кешбек {tier!.Percent:0.##}% за поповнення {MoneyText.Of(amount)} ₴");
        }

        private void Refund(UserEntity user, decimal amount, string reason, DateTimeOffset at)
        {
            var (balance, _) = _wallets[user.Id];
            if (balance < amount) TopUp(user, 300m, PaymentMethods.Cash, at.AddMilliseconds(-10));
            Post(user, at, LedgerTypes.Refund, PaymentMethods.Cash, amount, -amount, 0m, -amount,
                $"Повернення {MoneyText.Of(amount)} ₴ (готівкою): {reason}", reason: reason);
            var reversal = Math.Min(_wallets[user.Id].Bonus, BonusRules.CashbackFor(BonusTiers, amount));
            if (reversal > 0)
                Post(user, at.AddMilliseconds(1), LedgerTypes.BonusReversal, PaymentMethods.None, reversal, 0m, -reversal, 0m,
                    $"Скасування кешбеку за повернені {MoneyText.Of(amount)} ₴", reason: reason);
        }

        private void Adjust(UserEntity user, decimal amount, string reason, DateTimeOffset at) =>
            Post(user, at, LedgerTypes.Adjustment, PaymentMethods.None, Math.Abs(amount), amount, 0m, 0m,
                $"Коригування балансу {MoneyText.Signed(amount)} ₴: {reason}", reason: reason, actor: "demo-manager");

        private LedgerEntryEntity Post(UserEntity user, DateTimeOffset at, string type, string method, decimal amount, decimal balanceDelta,
            decimal bonusDelta, decimal desk, string description, SessionEntity? session = null, string? reason = null, string? actor = "demo-cashier",
            decimal debtDelta = 0m, long? eventId = null, Guid? deviceId = null)
        {
            var (balance, bonus) = _wallets[user.Id];
            balance += balanceDelta;
            bonus += bonusDelta;
            _wallets[user.Id] = (balance, bonus);
            var debt = (_debts.TryGetValue(user.Id, out var d) ? d : 0m) + debtDelta;
            _debts[user.Id] = debt;
            var row = new LedgerEntryEntity
            {
                CreatedUtc = at, UserId = user.Id, Type = type, Method = method, Amount = amount,
                BalanceDelta = balanceDelta, BonusDelta = bonusDelta, DeskAmount = desk, BalanceAfter = balance, BonusAfter = bonus,
                DebtDelta = debtDelta, DebtAfter = debt,
                SessionId = session?.Id, PackageId = session?.PackageId, DeviceId = session?.DeviceId ?? deviceId, EventId = eventId,
                Description = description, Reason = reason, ActorUsername = actor
            };
            LedgerRows.Add(row);
            return row;
        }

        /// <summary>Applies the player events in date order; balances end at 50 ₴ or more.</summary>
        private void ApplyPlayerEvents()
        {
            foreach (var e in _playerEvents.OrderBy(e => e.At).ThenBy(e => e.Order).ToList())
                e.Apply();
            foreach (var user in _users)
            {
                var (balance, _) = _wallets[user.Id];
                if (balance < 50m)
                    TopUp(user, Math.Ceiling((50m - balance) / 100m) * 100m + 100m, PaymentMethods.Card, _now.AddSeconds(-30)); // after all events
                (user.Balance, user.BonusPoints) = _wallets[user.Id];
                user.Debt = _debts.TryGetValue(user.Id, out var debt) ? debt : 0m;
            }
        }
    }
}
