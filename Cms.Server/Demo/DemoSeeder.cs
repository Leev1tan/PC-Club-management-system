using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

/// <summary>A seeded PC that the demo simulator drives.</summary>
public sealed record SimulatedPc(Guid Id, string Hostname, string DeviceKey, string Ip, bool AlwaysOffline, bool? Locked);

/// <summary>PausedSessions: seeded paused sessions (the simulator resumes them after a few minutes).</summary>
public sealed record DemoSeedResult(IReadOnlyList<SimulatedPc> Pcs, IReadOnlyList<Guid>? PausedSessions = null);

/// <summary>
/// Wipes the database and seeds a demo club with typical Ukrainian prices (UAH, 2026): 3 zones
/// (Standard 12 PCs, VIP 6, Bootcamp/Pro 2), zone tariffs with weekday-day / weekend-day / evening /
/// night prices, 3 h and 5 h packs, a morning pack and a night pack per zone, cashback on top-ups,
/// players whose balances and bonuses come from their ledger history (top-ups, cashback, sessions,
/// packages, a refund, a correction), about a week of sessions and live sessions (packages, a paused
/// session, an open-ended one). All data is fictional. Nothing here is used outside DEMO_MODE.
/// </summary>
public static class DemoSeeder
{
    /// <summary>AgentVersion of simulated PCs; also marks a database as a demo database.</summary>
    public const string AgentVersion = "demo-sim";

    /// <summary>This PC never comes online, so the map also shows the offline state.</summary>
    public const string OfflineHostname = "STD-12";

    private const int HistorySessions = 140;
    private const int HistoryDays = 9;
    private const double ActiveShare = 0.55;

    private sealed record PackSpec(string Name, decimal Price, int? Minutes, int? WindowStart = null, int? WindowEnd = null, int Days = 127);

    private sealed record ZoneSpec(
        string Name, string Color, string HostPrefix, int Count, string TariffName,
        decimal Base, decimal WeekdayDay, decimal WeekendDay, decimal Evening, decimal Night,
        int Step, decimal MinimumCharge, PackSpec[] Packs);

    private const int Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16, Sat = 32, Sun = 64;
    private const int MonThu = Mon | Tue | Wed | Thu, FriSun = Fri | Sat | Sun, MonFri = MonThu | Fri, All = 127;
    private const int H8 = 8 * 60, H12 = 12 * 60, H17 = 17 * 60, H22 = 22 * 60;

    private static readonly ZoneSpec[] Zones =
    {
        new("Standard", "#00f0ff", "STD", 12, "Стандарт", 60m, 45m, 55m, 70m, 40m, 1, 20m, new[]
        {
            new PackSpec("3 години", 150m, 180),
            new PackSpec("5 годин", 230m, 300),
            new PackSpec("Ранковий (3 год, до 12:00)", 99m, 180, H8, H12, MonFri),
            new PackSpec("Нічний 22:00–08:00", 250m, null, H22, H8),
        }),
        new("VIP", "#bf5af2", "VIP", 6, "VIP", 90m, 70m, 80m, 100m, 60m, 5, 30m, new[]
        {
            new PackSpec("3 години VIP", 240m, 180),
            new PackSpec("5 годин VIP", 370m, 300),
            new PackSpec("Нічний VIP 22:00–08:00", 380m, null, H22, H8),
        }),
        new("Bootcamp / Pro", "#ffe019", "PRO", 2, "Bootcamp", 120m, 95m, 110m, 140m, 80m, 15, 40m, new[]
        {
            new PackSpec("3 години Bootcamp", 330m, 180),
            new PackSpec("5 годин Bootcamp", 520m, 300),
            new PackSpec("Нічний Bootcamp 22:00–08:00", 500m, null, H22, H8),
        }),
    };

    public static readonly IReadOnlyList<BonusTier> BonusTiers = new[]
    {
        new BonusTier(200m, 5m), new BonusTier(500m, 10m), new BonusTier(1000m, 15m)
    };

    private static readonly (string Username, string DisplayName)[] Players =
    {
        (DemoOptions.PlayerLogin, "Demo Player"),
        ("nightowl", "Night Owl"),
        ("vika_gg", "Віка"),
        ("taras_ua", "Тарас"),
        ("frostbyte", "Frostbyte"),
        ("olena_k", "Олена К."),
        ("maks_pro", "Макс"),
        ("headshot", "Headshot Hero"),
    };

    /// <summary>
    /// True when the database is empty or was seeded by demo mode. Demo mode refuses to
    /// start on any other database, so DEMO_MODE=true can never wipe a real club.
    /// </summary>
    public static async Task<bool> IsDemoOrEmptyAsync(CmsDbContext db, CancellationToken ct = default)
    {
        if (await db.Devices.AnyAsync(d => d.AgentVersion == AgentVersion, ct)) return true;
        return !await db.Devices.AnyAsync(ct)
               && !await db.Users.AnyAsync(ct)
               && !await db.Zones.AnyAsync(ct)
               && !await db.Tariffs.AnyAsync(ct)
               && !await db.Packages.AnyAsync(ct)
               && !await db.Sessions.AnyAsync(ct)
               && !await db.Ledger.AnyAsync(ct)
               && !await db.StaffUsers.AnyAsync(ct);
    }

    public static Task<DemoSeedResult> ResetAsync(CmsDbContext db, DateTimeOffset now, Random rng, CancellationToken ct = default) =>
        ResetAsync(db, now, rng, ClubTimeZone.Default, ct);

    public static async Task<DemoSeedResult> ResetAsync(CmsDbContext db, DateTimeOffset now, Random rng, TimeZoneInfo zone, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Children first (foreign key order); the append-only tables are cleared the demo way
        await db.Commands.ExecuteDeleteAsync(ct);
        await db.Heartbeats.ExecuteDeleteAsync(ct);
        await LedgerGuard.ClearForDemoResetAsync(db, ct);
        await db.Transactions.ExecuteDeleteAsync(ct);
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
        db.ChangeTracker.Clear();

        var seed = new Seeding(db, now, rng, zone);
        var result = seed.Run();
        await db.SaveChangesAsync(ct);
        // Ledger rows one by one in date order, so that their ids follow the dates
        foreach (var row in seed.LedgerRows.OrderBy(r => r.CreatedUtc))
        {
            db.Ledger.Add(row);
            await db.SaveChangesAsync(ct);
        }
        await BonusRules.SaveAsync(db, BonusTiers, "demo", now, ct);
        db.ClubSettings.Add(new ClubSettingEntity { Key = ClubSettingKeys.LedgerBackfilled, Value = "demo", UpdatedUtc = now, UpdatedBy = "demo" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return result;
    }

    private static T Pick<T>(Random rng, params T[] items) => items[rng.Next(items.Length)];

    /// <summary>One seeding run (keeps the generated data together).</summary>
    private sealed class Seeding
    {
        private readonly CmsDbContext _db;
        private readonly DateTimeOffset _now;
        private readonly Random _rng;
        private readonly TimeZoneInfo _zone;
        private readonly List<(DeviceEntity Device, TariffEntity Tariff, TariffPricing Pricing, List<PackageEntity> Packs)> _pcs = new();
        private readonly Dictionary<Guid, (decimal Balance, decimal Bonus)> _wallets = new();
        private readonly List<(DateTimeOffset At, int Order, Action Apply)> _playerEvents = new();
        private List<UserEntity> _users = new();

        public List<LedgerEntryEntity> LedgerRows { get; } = new();

        public Seeding(CmsDbContext db, DateTimeOffset now, Random rng, TimeZoneInfo zone)
        {
            _db = db;
            _now = now;
            _rng = rng;
            _zone = zone;
        }

        public DemoSeedResult Run()
        {
            var pcs = SeedClub();
            SeedStaff();
            SeedPlayers();
            SeedHistory();
            var paused = SeedLiveSessions(pcs);
            ApplyPlayerEvents();
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
                _db.Zones.Add(zone);
                _db.Tariffs.Add(tariff);
                _db.TariffRules.AddRange(rules);
                var pricing = TariffPricing.From(tariff, rules);

                var packs = spec.Packs.Select((p, i) => new PackageEntity
                {
                    Id = Guid.NewGuid(), Name = p.Name, ZoneId = zone.Id, Price = p.Price, DurationMinutes = p.Minutes,
                    WindowStartMinute = p.WindowStart, WindowEndMinute = p.WindowEnd, DaysMask = p.Days, IsActive = true, SortOrder = i
                }).ToList();
                _db.Packages.AddRange(packs);

                if (z == 0)
                {
                    // A second tariff of the Standard zone that the cashier can choose
                    var student = new TariffEntity
                    {
                        Id = Guid.NewGuid(), Name = "Студентський", PricePerHour = 60m, ZoneId = zone.Id, BillingStepMinutes = 1, MinimumCharge = 20m
                    };
                    _db.Tariffs.Add(student);
                    _db.TariffRules.Add(Rule(student, 0, "Студентам Пн–Пт 08–17", MonFri, H8, H17, 35m));
                }

                for (var i = 1; i <= spec.Count; i++)
                {
                    var hostname = $"{spec.HostPrefix}-{i:00}";
                    var offline = hostname == OfflineHostname;
                    var ip = $"10.3.3.{ipSuffix++}";
                    var deviceKey = $"demo-{Guid.NewGuid():N}"; // the database stores only its hash
                    var device = new DeviceEntity
                    {
                        Id = Guid.NewGuid(),
                        Hostname = hostname,
                        OsVersion = "Microsoft Windows NT 10.0.22631.0",
                        AgentVersion = AgentVersion,
                        DeviceKey = DeviceKeys.Hash(deviceKey),
                        Status = DeviceStatuses.Approved,
                        RegisteredUtc = _now.AddDays(-30),
                        LastSeenUtc = offline ? _now.AddHours(-3).AddMinutes(-12) : _now,
                        LastIp = ip,
                        ZoneId = zone.Id,
                        PositionX = (i - 1) % 6,
                        PositionY = row + (i - 1) / 6
                    };
                    _db.Devices.Add(device);
                    _pcs.Add((device, tariff, pricing, packs));
                    pcs.Add(new SimulatedPc(device.Id, hostname, deviceKey, ip, offline, null));
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
            var staffHash = Passwords.Hash(DemoOptions.StaffPassword);
            foreach (var (username, displayName, role) in DemoOptions.StaffAccounts)
            {
                _db.StaffUsers.Add(new StaffUserEntity
                {
                    Id = Guid.NewGuid(), Username = username, DisplayName = displayName,
                    PasswordHash = staffHash, Role = role, IsActive = true, CreatedUtc = _now
                });
            }
        }

        private void SeedPlayers()
        {
            // Password "demo" for all of them; demo data only
            var passwordHash = Passwords.Hash(DemoOptions.PlayerPassword);
            _users = Players.Select(p => new UserEntity
            {
                Id = Guid.NewGuid(),
                Username = p.Username,
                DisplayName = p.DisplayName,
                PasswordHash = passwordHash,
                CreatedUtc = _now.AddDays(-_rng.Next(15, 120)).AddMinutes(-_rng.Next(0, 1440))
            }).ToList();
            _db.Users.AddRange(_users);
            foreach (var user in _users) _wallets[user.Id] = (0m, 0m);

            // Top-ups in cash or by card over the last two weeks (cashback by the demo tiers)
            foreach (var user in _users)
            {
                var count = _rng.Next(1, 4);
                for (var i = 0; i < count; i++)
                {
                    var at = _now.AddDays(-_rng.Next(9, 15)).AddMinutes(-_rng.Next(0, 1440));
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

        private void SeedHistory()
        {
            var online = _pcs.Where(p => p.Device.Hostname != OfflineHostname).ToList();
            for (var i = 0; i < HistorySessions; i++)
            {
                var pc = online[_rng.Next(online.Count)];
                var user = _rng.NextDouble() < 0.35 ? _users[_rng.Next(_users.Count)] : null;
                var end = _now.AddMinutes(-_rng.Next(5, HistoryDays * 24 * 60));
                var roll = _rng.NextDouble();

                if (roll < 0.3 && TryPackage(pc, end.AddMinutes(-_rng.Next(60, 300)), out var package, out var terms) && terms.EndUtc < _now)
                {
                    var s = AddSession(pc, terms.StartUtc, terms.EndUtc, package.Price, true, package, terms.Minutes, user, "ended", cutoffUtc: terms.CutoffUtc);
                    Pay(s, user, package.Price, LedgerTypes.PackagePurchase, $"Пакет «{package.Name}» на {pc.Device.Hostname} — {package.Price} ₴");
                    continue;
                }

                var minutes = Pick(_rng, 30, 60, 60, 90, 120, 120, 180, 240, 300);
                var start = end.AddMinutes(-minutes);
                if (roll > 0.9)
                {
                    // Post-paid: billed at the end per rule, rounded to the tariff step
                    var played = minutes + _rng.Next(-7, 8);
                    var stop = start.AddMinutes(played).AddSeconds(_rng.Next(0, 60));
                    var cost = TariffCalculator.QuotePlayed(new[] { (pc.Pricing, start, stop) }, pc.Pricing, _zone).Total;
                    var s = AddSession(pc, start, stop, cost, false, null, null, user, "ended");
                    Pay(s, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} ({played} хв) — {cost} ₴");
                }
                else
                {
                    var cost = TariffCalculator.QuoteFixed(pc.Pricing, start, minutes, _zone).Total;
                    var s = AddSession(pc, start, end, cost, true, null, minutes, user, "ended");
                    Pay(s, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} ({minutes} хв, {pc.Tariff.Name}) — {cost} ₴");
                }
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

                var user = _rng.NextDouble() < 0.4 ? _users[_rng.Next(_users.Count)] : null;
                var locked = false;
                if (i == activeCount - 1)
                {
                    // One open-ended (post-paid) session, billed when it is ended
                    AddSession(pc, _now.AddMinutes(-_rng.Next(10, 200)), null, 0m, false, null, null, user, "active");
                }
                else if (i == activeCount - 2)
                {
                    // One paused session (the player went out for a moment); the simulator resumes it
                    var start = _now.AddMinutes(-_rng.Next(30, 50));
                    var cost = TariffCalculator.QuoteFixed(pc.Pricing, start, 120, _zone).Total;
                    var s = AddSession(pc, start, start.AddMinutes(120), cost, true, null, 120, user, "active", pausedUtc: _now.AddMinutes(-4));
                    Pay(s, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} (120 хв, {pc.Tariff.Name}) — {cost} ₴");
                    paused.Add(s.Id);
                    locked = true;
                }
                else if (i % 3 == 2 && TryPackage(pc, _now.AddMinutes(-_rng.Next(10, 90)), out var package, out var terms) && terms.EndUtc > _now.AddMinutes(5))
                {
                    var s = AddSession(pc, terms.StartUtc, terms.EndUtc, package.Price, true, package, terms.Minutes, user, "active", cutoffUtc: terms.CutoffUtc);
                    Pay(s, user, package.Price, LedgerTypes.PackagePurchase, $"Пакет «{package.Name}» на {pc.Device.Hostname} — {package.Price} ₴");
                }
                else
                {
                    var duration = Pick(_rng, 60, 60, 120, 120, 180, 300);
                    // The first two expire within minutes, so auto-lock is visible soon after a reset
                    var remaining = i < 2 ? _rng.Next(2, 6) : _rng.Next(8, duration - 4);
                    var start = _now.AddMinutes(remaining - duration);
                    var cost = TariffCalculator.QuoteFixed(pc.Pricing, start, duration, _zone).Total;
                    var s = AddSession(pc, start, _now.AddMinutes(remaining), cost, true, null, duration, user, "active");
                    Pay(s, user, cost, LedgerTypes.SessionCharge, $"Сесія на {pc.Device.Hostname} ({duration} хв, {pc.Tariff.Name}) — {cost} ₴");
                }
                pcs[index] = pcs[index] with { Locked = locked };
            }
            return paused;
        }

        private bool TryPackage((DeviceEntity Device, TariffEntity Tariff, TariffPricing Pricing, List<PackageEntity> Packs) pc,
            DateTimeOffset start, out PackageEntity package, out PackageTerms terms)
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

        private SessionEntity AddSession((DeviceEntity Device, TariffEntity Tariff, TariffPricing Pricing, List<PackageEntity> Packs) pc,
            DateTimeOffset start, DateTimeOffset? end, decimal cost, bool prepaid, PackageEntity? package, int? paidMinutes,
            UserEntity? user, string status, DateTimeOffset? pausedUtc = null, DateTimeOffset? cutoffUtc = null)
        {
            var session = new SessionEntity
            {
                Id = Guid.NewGuid(), DeviceId = pc.Device.Id, TariffId = pc.Tariff.Id, UserId = user?.Id,
                StartUtc = start, EndUtc = end, Status = status, TotalCost = cost, IsPrepaid = prepaid,
                PackageId = package?.Id, PaidMinutes = paidMinutes, PausedUtc = pausedUtc,
                CutoffUtc = cutoffUtc
            };
            _db.Sessions.Add(session);
            var segmentEnd = pausedUtc ?? (status == "ended" ? end : null);
            _db.SessionSegments.Add(new SessionSegmentEntity
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
            Post(user, at, LedgerTypes.TopUp, method, amount, amount, 0m, amount, $"Поповнення балансу +{amount} ₴");
            var tier = BonusRules.TierFor(BonusTiers, amount);
            var bonus = BonusRules.CashbackFor(BonusTiers, amount);
            if (bonus > 0)
                Post(user, at.AddMilliseconds(1), LedgerTypes.BonusAccrual, PaymentMethods.None, bonus, 0m, bonus, 0m,
                    $"Кешбек {tier!.Percent:0.##}% за поповнення {amount} ₴");
        }

        private void Refund(UserEntity user, decimal amount, string reason, DateTimeOffset at)
        {
            var (balance, _) = _wallets[user.Id];
            if (balance < amount) TopUp(user, 300m, PaymentMethods.Cash, at.AddMilliseconds(-10));
            Post(user, at, LedgerTypes.Refund, PaymentMethods.Cash, amount, -amount, 0m, -amount,
                $"Повернення {amount} ₴ (готівкою): {reason}", reason: reason);
            var reversal = Math.Min(_wallets[user.Id].Bonus, BonusRules.CashbackFor(BonusTiers, amount));
            if (reversal > 0)
                Post(user, at.AddMilliseconds(1), LedgerTypes.BonusReversal, PaymentMethods.None, reversal, 0m, -reversal, 0m,
                    $"Скасування кешбеку за повернені {amount} ₴", reason: reason);
        }

        private void Adjust(UserEntity user, decimal amount, string reason, DateTimeOffset at) =>
            Post(user, at, LedgerTypes.Adjustment, PaymentMethods.None, Math.Abs(amount), amount, 0m, 0m,
                $"Коригування балансу {amount:+0.##;-0.##} ₴: {reason}", reason: reason, actor: "demo-manager");

        private void Post(UserEntity user, DateTimeOffset at, string type, string method, decimal amount, decimal balanceDelta,
            decimal bonusDelta, decimal desk, string description, SessionEntity? session = null, string? reason = null, string? actor = "demo-cashier")
        {
            var (balance, bonus) = _wallets[user.Id];
            balance += balanceDelta;
            bonus += bonusDelta;
            _wallets[user.Id] = (balance, bonus);
            LedgerRows.Add(new LedgerEntryEntity
            {
                CreatedUtc = at, UserId = user.Id, Type = type, Method = method, Amount = amount,
                BalanceDelta = balanceDelta, BonusDelta = bonusDelta, DeskAmount = desk, BalanceAfter = balance, BonusAfter = bonus,
                SessionId = session?.Id, PackageId = session?.PackageId, DeviceId = session?.DeviceId,
                Description = description, Reason = reason, ActorUsername = actor
            });
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
            }
        }
    }
}
