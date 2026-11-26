using Cms.Server.Controllers;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Demo;

/// <summary>A seeded PC that the demo simulator drives.</summary>
public sealed record SimulatedPc(Guid Id, string Hostname, string DeviceKey, string Ip, bool AlwaysOffline, bool? Locked);

public sealed record DemoSeedResult(IReadOnlyList<SimulatedPc> Pcs);

/// <summary>
/// Wipes the database and seeds a demo club: 3 zones (Standard 12 PCs, VIP 6, Bootcamp/Pro 2),
/// zone tariffs plus a night tariff, players, session history and live sessions.
/// All data is fictional. Nothing here is used outside DEMO_MODE.
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

    private sealed record ZoneSpec(string Name, string Color, string HostPrefix, int Count, string TariffName, decimal PricePerHour);

    private static readonly ZoneSpec[] Zones =
    {
        new("Standard", "#00f0ff", "STD", 12, "Стандарт", 40m),
        new("VIP", "#bf5af2", "VIP", 6, "VIP", 70m),
        new("Bootcamp / Pro", "#ffe019", "PRO", 2, "Bootcamp", 100m),
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
               && !await db.Sessions.AnyAsync(ct);
    }

    public static async Task<DemoSeedResult> ResetAsync(CmsDbContext db, DateTimeOffset now, Random rng, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Children first (foreign key order)
        await db.Commands.ExecuteDeleteAsync(ct);
        await db.Heartbeats.ExecuteDeleteAsync(ct);
        await db.Transactions.ExecuteDeleteAsync(ct);
        await db.Sessions.ExecuteDeleteAsync(ct);
        await db.Devices.ExecuteDeleteAsync(ct);
        await db.Tariffs.ExecuteDeleteAsync(ct);
        await db.Users.ExecuteDeleteAsync(ct);
        await db.Zones.ExecuteDeleteAsync(ct);
        db.ChangeTracker.Clear();

        var result = Seed(db, now, rng);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return result;
    }

    private static DemoSeedResult Seed(CmsDbContext db, DateTimeOffset now, Random rng)
    {
        var pcs = new List<SimulatedPc>();
        var devices = new List<(DeviceEntity Device, TariffEntity Tariff)>();

        // Zones, one tariff per zone, PCs
        var ipSuffix = 101;
        var row = 0;
        for (var z = 0; z < Zones.Length; z++)
        {
            var spec = Zones[z];
            var zone = new ZoneEntity { Id = Guid.NewGuid(), Name = spec.Name, Color = spec.Color, SortOrder = z };
            var tariff = new TariffEntity
            {
                Id = Guid.NewGuid(), Name = spec.TariffName, PricePerHour = spec.PricePerHour,
                IsDefault = z == 0, ZoneId = zone.Id
            };
            db.Zones.Add(zone);
            db.Tariffs.Add(tariff);

            for (var i = 1; i <= spec.Count; i++)
            {
                var hostname = $"{spec.HostPrefix}-{i:00}";
                var offline = hostname == OfflineHostname;
                var ip = $"10.3.3.{ipSuffix++}";
                var device = new DeviceEntity
                {
                    Id = Guid.NewGuid(),
                    Hostname = hostname,
                    OsVersion = "Microsoft Windows NT 10.0.22631.0",
                    AgentVersion = AgentVersion,
                    DeviceKey = $"demo-{Guid.NewGuid():N}",
                    LastSeenUtc = offline ? now.AddHours(-3).AddMinutes(-12) : now,
                    LastIp = ip,
                    ZoneId = zone.Id,
                    PositionX = (i - 1) % 6,
                    PositionY = row + (i - 1) / 6
                };
                db.Devices.Add(device);
                devices.Add((device, tariff));
                pcs.Add(new SimulatedPc(device.Id, hostname, device.DeviceKey, ip, offline, null));
            }
            row += (spec.Count + 5) / 6 + 1;
        }

        var night = new TariffEntity { Id = Guid.NewGuid(), Name = "Нічний (22:00–08:00)", PricePerHour = 25m, ZoneId = null };
        db.Tariffs.Add(night);

        // Players (password "demo" for all of them; demo data only)
        var passwordHash = UsersController.HashPassword(DemoOptions.PlayerPassword);
        var users = Players.Select(p => new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = p.Username,
            DisplayName = p.DisplayName,
            PasswordHash = passwordHash,
            BonusPoints = rng.Next(0, 40) * 5,
            CreatedUtc = now.AddDays(-rng.Next(10, 120)).AddMinutes(-rng.Next(0, 1440))
        }).ToList();
        db.Users.AddRange(users);
        var balances = users.ToDictionary(u => u.Id, _ => 0m);

        void TopUp(UserEntity user, decimal amount, DateTimeOffset at)
        {
            balances[user.Id] += amount;
            db.Transactions.Add(new TransactionEntity
            {
                Id = Guid.NewGuid(), UserId = user.Id, Amount = amount, Type = "topup",
                Description = $"Поповнення балансу +{amount} ₴", CreatedUtc = at
            });
        }

        void Charge(UserEntity user, decimal amount, string hostname, int minutes, DateTimeOffset at)
        {
            balances[user.Id] -= amount;
            db.Transactions.Add(new TransactionEntity
            {
                Id = Guid.NewGuid(), UserId = user.Id, Amount = -amount, Type = "session_charge",
                Description = $"Сесія на {hostname} ({minutes} хв) — {amount} ₴", CreatedUtc = at
            });
        }

        foreach (var user in users)
        {
            var count = rng.Next(1, 4);
            for (var i = 0; i < count; i++)
                TopUp(user, Pick(rng, 100m, 200m, 300m, 500m), now.AddDays(-rng.Next(1, 14)).AddMinutes(-rng.Next(0, 1440)));
        }

        var online = devices.Where(d => d.Device.Hostname != OfflineHostname).ToList();

        // Ended sessions over the last days (finance tab, revenue)
        for (var i = 0; i < HistorySessions; i++)
        {
            var (device, zoneTariff) = online[rng.Next(online.Count)];
            var minutes = Pick(rng, 30, 60, 60, 90, 120, 120, 180, 240, 300);
            var end = now.AddMinutes(-rng.Next(5, HistoryDays * 24 * 60));
            var start = end.AddMinutes(-minutes);
            var tariff = IsNight(start) ? night : zoneTariff;
            var cost = Math.Round(tariff.PricePerHour * minutes / 60m, 2);
            var user = rng.NextDouble() < 0.35 ? users[rng.Next(users.Count)] : null;
            db.Sessions.Add(new SessionEntity
            {
                Id = Guid.NewGuid(), DeviceId = device.Id, TariffId = tariff.Id, UserId = user?.Id,
                StartUtc = start, EndUtc = end, Status = "ended", TotalCost = cost, IsPrepaid = true
            });
            if (user != null) Charge(user, cost, device.Hostname, minutes, start);
        }

        // Live sessions on about half of the online PCs; the rest wait at the lock screen
        var shuffled = online.OrderBy(_ => rng.Next()).ToList();
        var activeCount = (int)Math.Round(online.Count * ActiveShare);
        for (var i = 0; i < shuffled.Count; i++)
        {
            var (device, tariff) = shuffled[i];
            var index = pcs.FindIndex(p => p.Id == device.Id);
            if (i >= activeCount)
            {
                pcs[index] = pcs[index] with { Locked = true };
                continue;
            }

            var user = rng.NextDouble() < 0.4 ? users[rng.Next(users.Count)] : null;
            SessionEntity session;
            if (i == activeCount - 1)
            {
                // One open-ended (post-paid) session, billed when it is ended
                session = new SessionEntity
                {
                    Id = Guid.NewGuid(), DeviceId = device.Id, TariffId = tariff.Id, UserId = user?.Id,
                    StartUtc = now.AddMinutes(-rng.Next(10, 200)), EndUtc = null,
                    Status = "active", TotalCost = 0, IsPrepaid = false
                };
            }
            else
            {
                var duration = Pick(rng, 60, 60, 120, 120, 180, 300);
                // The first two expire within minutes, so auto-lock is visible soon after a reset
                var remaining = i < 2 ? rng.Next(2, 6) : rng.Next(8, duration - 4);
                var start = now.AddMinutes(remaining - duration);
                var cost = Math.Round(tariff.PricePerHour * duration / 60m, 2);
                session = new SessionEntity
                {
                    Id = Guid.NewGuid(), DeviceId = device.Id, TariffId = tariff.Id, UserId = user?.Id,
                    StartUtc = start, EndUtc = now.AddMinutes(remaining),
                    Status = "active", TotalCost = cost, IsPrepaid = true
                };
                if (user != null) Charge(user, cost, device.Hostname, duration, start);
            }
            db.Sessions.Add(session);
            pcs[index] = pcs[index] with { Locked = false };
        }

        // Keep every balance positive and plausible
        foreach (var user in users)
        {
            if (balances[user.Id] < 50m)
                TopUp(user, Math.Ceiling((50m - balances[user.Id]) / 100m) * 100m + 100m, now.AddDays(-rng.Next(1, 5)));
            user.Balance = balances[user.Id];
        }

        return new DemoSeedResult(pcs);
    }

    /// <summary>Night hours in club time (the demo club uses UTC+2).</summary>
    private static bool IsNight(DateTimeOffset utc)
    {
        var hour = (utc.UtcDateTime.Hour + 2) % 24;
        return hour >= 22 || hour < 8;
    }

    private static T Pick<T>(Random rng, params T[] items) => items[rng.Next(items.Length)];
}
