using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The lock screen reports the teardown of an ended session; the server writes it to the audit log once.</summary>
public sealed class TeardownEventTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public TeardownEventTests(CmsFactory factory) : base(factory) { }

    [Fact]
    public async Task A_teardown_note_is_audited_once_with_what_was_closed_and_removed()
    {
        var owner = await _factory.LoginOwnerAsync();
        var pc = await TestPc.AddAsync(_factory);
        var id = Guid.NewGuid();
        var note = new
        {
            id, type = "session_teardown", occurredUtc = DateTimeOffset.UtcNow, windowsUser = "ClubUser",
            killed = new[] { "steam", "steamwebhelper", "cs2", "Discord", "chrome" }, killFailed = 1,
            cleanupEnabled = true, removedFiles = 31, removedDirectories = 4,
            removedPaths = new[] { @"C:\Users\ClubUser\AppData\Local\Google\Chrome\User Data" }, cleanupErrors = 2
        };
        var res = await pc.PostAsync($"/api/devices/{pc.Id}/events", note);
        Assert.True((await JsonAsync(res)).GetProperty("recorded").GetBoolean());
        Assert.False((await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/events", note))).GetProperty("recorded").GetBoolean());

        var rows = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=device.session_teardown")).EnumerateArray()
            .Where(a => a.GetProperty("targetId").GetString() == pc.Id.ToString()).ToList();
        var row = Assert.Single(rows);
        var summary = row.GetProperty("summary").GetString()!;
        Assert.StartsWith($"Session teardown on {pc.Hostname} at ", summary);
        Assert.EndsWith("5 program(s) closed (steam, steamwebhelper, cs2, Discord, chrome), 1 not closed; cleanup removed 31 file(s) and 4 folder(s) in 1 path(s), 2 error(s)", summary);

        // Cleanup off, nothing to close; long lists are clipped
        var many = Enumerable.Range(0, 50).Select(i => $"proc{i}").ToArray();
        await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/events", new { id = Guid.NewGuid(), type = "session_teardown", occurredUtc = DateTimeOffset.UtcNow, killed = many, cleanupEnabled = false }));
        await JsonAsync(await pc.PostAsync($"/api/devices/{pc.Id}/events", new { id = Guid.NewGuid(), type = "session_teardown", occurredUtc = DateTimeOffset.UtcNow, cleanupEnabled = false }));
        var summaries = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=device.session_teardown")).EnumerateArray()
            .Where(a => a.GetProperty("targetId").GetString() == pc.Id.ToString()).Select(a => a.GetProperty("summary").GetString()!).ToList();
        Assert.Contains(summaries, s => s.EndsWith("30 program(s) closed (proc0, proc1, proc2, proc3, proc4, proc5, proc6, proc7, ...); cleanup off"));
        Assert.Contains(summaries, s => s.EndsWith("no program to close; cleanup off"));

        // Only the agent of an approved PC, for itself
        var other = await TestPc.AddAsync(_factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"/api/devices/{pc.Id}/events", note)).StatusCode);
    }
}
