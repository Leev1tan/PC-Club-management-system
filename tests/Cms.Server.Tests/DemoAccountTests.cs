using Cms.Server.Accounts;
using Cms.Server.Billing;
using Cms.Server.Demo;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The demo has only fake club accounts with random secrets; the PCs that play a paid game hold a lease.</summary>
public sealed class DemoAccountTests
{
    [Theory]
    [InlineData("09:01")]
    [InlineData("21:01")]
    [InlineData("00:01")]
    [InlineData("now")]
    public async Task The_demo_accounts_are_fake_and_the_leases_follow_the_sessions(string clubTime)
    {
        var now = ClubTimePoints.At(clubTime);
        var vault = GameAccountVault.Ephemeral();
        using var testDb = new TestDb();
        await using (var db = testDb.NewShiftContext()) await DemoSeeder.ResetAsync(db, now, new Random(42), ClubTimeZone.Default, default, vault);
        await using var check = testDb.NewContext();
        var accounts = await check.GameAccounts.AsNoTracking().ToListAsync();
        Assert.Equal(8, accounts.Count);
        var logins = accounts.Select(a => vault.TryUnprotect(a.LoginCipher)!).ToList();
        Assert.All(logins, l => Assert.Matches("^demo-(steam|epic)-0[1-6]$", l));
        var secrets = accounts.Where(a => a.SecretCipher != null).Select(a => vault.TryUnprotect(a.SecretCipher)!).ToList();
        Assert.Equal(7, secrets.Count);
        Assert.All(secrets, s => Assert.Equal(24, s.Length));
        Assert.Equal(7, secrets.Distinct().Count());
        Assert.Equal(3, await check.Games.CountAsync(g => g.AccountPlatform == "steam"));
        // Every open lease belongs to a running session of its PC, and the PC plays a game that needs an account
        var open = await check.GameAccountLeases.AsNoTracking().Where(l => l.EndedUtc == null).ToListAsync();
        var active = await check.Sessions.AsNoTracking().Where(s => s.Status == "active").ToListAsync();
        Assert.All(open, l => Assert.Contains(active, s => s.Id == l.SessionId && s.DeviceId == l.DeviceId));
        Assert.Equal(open.Count, open.Select(l => l.AccountId).Distinct().Count());
        Assert.All(open, l => Assert.NotNull(l.DeliveredUtc));
        Assert.Equal(0, await GameAccountService.EndStaleAsync(check, now, "recovery"));
        // The audit log names the accounts by the mask only
        var audit = string.Join("\n", await check.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("game_account.")).Select(a => a.Summary + a.DetailsJson).ToListAsync());
        Assert.Contains("de•••01", audit);
        Assert.DoesNotContain("demo-steam", audit);
        Assert.DoesNotContain(secrets, s => audit.Contains(s));
    }
}
