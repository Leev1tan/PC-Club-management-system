using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Data;
using Cms.Server.Shifts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Hash chain of the audit log: verify finds an edited, removed or inserted row; the legacy seal; shift anchors.</summary>
public sealed class AuditChainTests : IDisposable
{
    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    private async Task<List<long>> WriteAsync(int count, string prefix = "test")
    {
        await using var db = _testDb.NewContext();
        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var entry = new AuditLogEntity
            {
                CreatedUtc = DateTimeOffset.UtcNow.AddTicks(i * 7), ActorId = Guid.NewGuid(), ActorUsername = "cashier", ActorRole = "Cashier",
                Action = $"{prefix}.action", TargetType = "player", TargetId = i.ToString(), Summary = $"Поповнення +{i * 10} ₴",
                DetailsJson = $$"""{"amount":{{i * 10}},"note":"«лапки» \"quotes\""}""", Ip = "10.0.0.5"
            };
            await AuditChain.AppendAsync(db, entry);
            ids.Add(entry.Id);
        }
        return ids;
    }

    private async Task<AuditChain.VerifyResult> VerifyAsync()
    {
        await using var db = _testDb.NewContext();
        return await AuditChain.VerifyAsync(db);
    }

    private async Task SqlAsync(string sql)
    {
        await using var db = _testDb.NewContext();
        await db.Database.ExecuteSqlRawAsync(sql); // TestDb has no triggers: this is what a database administrator could do
    }

    [Fact]
    public async Task Verify_detects_an_edited_row_a_removed_row_and_an_inserted_row()
    {
        var ids = await WriteAsync(6);
        var ok = await VerifyAsync();
        Assert.True(ok.Ok);
        Assert.Equal((6, ids[^1], 0), (ok.Rows, ok.HeadId, ok.AnchorsChecked));
        await using (var db = _testDb.NewContext())
        {
            var rows = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
            Assert.Equal(AuditChain.Genesis, rows[0].PrevHash);
            Assert.All(rows.Skip(1).Zip(rows), pair => Assert.Equal(pair.Second.Hash, pair.First.PrevHash));
            Assert.Equal(rows[^1].Hash, ok.HeadHash);
            Assert.Matches("^[0-9a-f]{64}$", rows[0].Hash!);
        }

        // Edited content: the row itself is reported
        await SqlAsync($"UPDATE \"AuditLog\" SET \"Summary\" = 'Поповнення +1 ₴' WHERE \"Id\" = {ids[2]}");
        var edited = await VerifyAsync();
        Assert.Equal((false, ids[2], "hash"), (edited.Ok, edited.FirstBrokenId, edited.Problem));
        await SqlAsync($"UPDATE \"AuditLog\" SET \"Summary\" = 'Поповнення +20 ₴' WHERE \"Id\" = {ids[2]}");
        Assert.True((await VerifyAsync()).Ok); // the original content is valid again

        // A changed time or actor counts as well
        await SqlAsync($"UPDATE \"AuditLog\" SET \"ActorUsername\" = 'owner' WHERE \"Id\" = {ids[1]}");
        Assert.Equal(ids[1], (await VerifyAsync()).FirstBrokenId);
        await SqlAsync($"UPDATE \"AuditLog\" SET \"ActorUsername\" = 'cashier' WHERE \"Id\" = {ids[1]}");

        // Removed row: the next row's link is broken
        await SqlAsync($"DELETE FROM \"AuditLog\" WHERE \"Id\" = {ids[3]}");
        var removed = await VerifyAsync();
        Assert.Equal((false, ids[4], "link"), (removed.Ok, removed.FirstBrokenId, removed.Problem));

        // A row inserted without a hash is reported too
        using var other = new TestDb();
        await using (var db = other.NewContext())
        {
            await AuditChain.AppendAsync(db, new AuditLogEntity { Action = "a" });
            db.AuditLog.Add(new AuditLogEntity { CreatedUtc = DateTimeOffset.UtcNow, Action = "planted" });
            await db.SaveChangesAsync();
            var planted = await AuditChain.VerifyAsync(db);
            Assert.Equal((false, "missing", 2), (planted.Ok, planted.Problem, planted.Rows));
        }
    }

    [Fact]
    public async Task Rows_of_an_older_version_are_sealed_once_and_the_chain_continues()
    {
        await using (var db = _testDb.NewContext())
        {
            for (var i = 0; i < 3; i++)
                db.AuditLog.Add(new AuditLogEntity { CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10 + i).AddTicks(3), Action = "auth.login", ActorUsername = "owner", Summary = $"old {i}" });
            await db.SaveChangesAsync();
            await AuditLogGuard.EnsureSqliteTriggersAsync(db);
            Assert.False((await AuditChain.VerifyAsync(db)).Ok);

            Assert.Equal(3, await AuditChain.SealLegacyAsync(db));
            Assert.Equal(0, await AuditChain.SealLegacyAsync(db)); // once
            Assert.True(await db.ClubSettings.AnyAsync(s => s.Key == ClubSettingKeys.AuditChainSealed));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Summary\" = 'x'")); // trigger is back
        }
        await WriteAsync(2);
        var result = await VerifyAsync();
        Assert.True(result.Ok);
        Assert.Equal(5, result.Rows);
    }

    [Fact]
    public async Task A_closed_shift_stores_the_head_hash_and_verify_checks_it()
    {
        await WriteAsync(3);
        long shiftId;
        await using (var db = _testDb.NewContext())
        {
            var actor = new Cms.Server.Billing.StaffActor(Guid.NewGuid(), "cashier");
            var service = new ShiftService(db, actor: actor);
            shiftId = (await service.OpenAsync(500m, null)).Value!.Id;
            var closed = (await service.CloseAsync(shiftId, 500m, null, isManager: false)).Value!;
            var (headId, headHash) = await AuditChain.HeadAsync(db);
            Assert.NotNull(closed.AuditHeadHash);
            Assert.Equal(headId, closed.AuditHeadId); // the last row before the close
            Assert.Equal(64, headHash!.Length);
        }
        var ok = await VerifyAsync();
        Assert.True(ok.Ok);
        Assert.Equal(1, ok.AnchorsChecked);

        // Rewriting the whole chain from the first row keeps every link valid, but not the anchor
        await using (var db = _testDb.NewContext())
        {
            var rows = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
            var prev = AuditChain.Genesis;
            foreach (var row in rows)
            {
                if (row.Id == rows[0].Id) row.Summary = "rewritten";
                var hash = AuditChain.Compute(prev, row);
                var p = prev;
                await db.AuditLog.Where(a => a.Id == row.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Summary, row.Summary).SetProperty(a => a.PrevHash, p).SetProperty(a => a.Hash, hash));
                prev = hash;
            }
        }
        var rewritten = await VerifyAsync();
        Assert.Equal((false, "anchor"), (rewritten.Ok, rewritten.Problem));
    }
}

public sealed class AuditChainApiTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public AuditChainApiTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task Only_the_owner_verifies_the_chain_and_every_entry_shows_its_hash()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var player = await PlayerAsync(cashier);
        await JsonAsync(await cashier.PostAsJsonAsync($"/api/users/{player}/topup", new { amount = 100 }));

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/audit/verify")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/audit/verify")).StatusCode);
        var verify = await JsonAsync(await owner.GetAsync("/api/audit/verify"));
        Assert.True(verify.GetProperty("ok").GetBoolean(), verify.ToString());
        Assert.True(verify.GetProperty("rows").GetInt32() >= 5);

        var entries = (await manager.GetFromJsonAsync<JsonElement>("/api/audit?take=500")).EnumerateArray().ToList();
        Assert.All(entries, e => Assert.Matches("^[0-9a-f]{64}$", e.GetProperty("hash").GetString()!));
        Assert.Equal(verify.GetProperty("headHash").GetString(), entries[0].GetProperty("hash").GetString());
        Assert.Equal(entries[1].GetProperty("hash").GetString(), entries[0].GetProperty("prevHash").GetString());
    }
}

public sealed class PostgresAuditChainTests : IDisposable
{
    private readonly Lazy<PostgresDb> _db = new(() => new PostgresDb());

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
    }

    [PostgresFact]
    public async Task Parallel_writes_keep_one_chain_and_the_seal_and_verify_work_on_postgres()
    {
        // Rows of an older version (no hashes), then the one-time seal
        await using (var db = _db.Value.NewContext())
        {
            for (var i = 0; i < 3; i++)
                await db.Database.ExecuteSqlRawAsync("INSERT INTO \"AuditLog\" (\"CreatedUtc\", \"Action\", \"Summary\") VALUES (now() - interval '1 hour', 'auth.login', {0})", $"old {i}");
            Assert.Equal(3, await AuditChain.SealLegacyAsync(db));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Summary\" = 'x'")); // trigger enabled again
        }

        // 24 writers at the same time: the advisory lock keeps the chain linear
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(async () =>
        {
            await using var db = _db.Value.NewContext();
            await AuditChain.AppendAsync(db, new AuditLogEntity { CreatedUtc = DateTimeOffset.UtcNow, Action = "parallel.write", Summary = $"#{i}", ActorId = Guid.NewGuid() });
        })));

        await using var check = _db.Value.NewContext();
        var result = await AuditChain.VerifyAsync(check);
        Assert.True(result.Ok, $"{result.Problem} at {result.FirstBrokenId}");
        Assert.Equal(27, result.Rows);

        // An administrator who pauses the trigger and edits a row is caught
        var target = await check.AuditLog.AsNoTracking().OrderBy(a => a.Id).Skip(10).Select(a => a.Id).FirstAsync();
        await check.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLog\" DISABLE TRIGGER audit_log_append_only");
        await check.Database.ExecuteSqlRawAsync($"UPDATE \"AuditLog\" SET \"Summary\" = 'edited' WHERE \"Id\" = {target}");
        await check.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLog\" ENABLE TRIGGER audit_log_append_only");
        var broken = await AuditChain.VerifyAsync(check);
        Assert.Equal((false, target, "hash"), (broken.Ok, broken.FirstBrokenId, broken.Problem));
    }
}
