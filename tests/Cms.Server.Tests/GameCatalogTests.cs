using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Data;
using Cms.Server.Devices;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Game catalog (spec area 8): roles, validation, audit, the catalog of a PC, game plays and top games.</summary>
public sealed class GameCatalogTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public GameCatalogTests(CmsFactory factory) : base(factory) { }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..5]}";

    private async Task<string> ZoneAsync(HttpClient owner) =>
        (await JsonAsync(await owner.PostAsJsonAsync("/api/zones", new { name = Unique("Zone") }))).GetProperty("id").GetString()!;

    private static object Steam(string name, int appId = 730, bool allZones = true, string[]? zoneIds = null, bool isActive = true) =>
        new { name, category = "Шутери", kind = "steam", steamAppId = appId, processName = "cs2.exe", allZones, zoneIds, isActive };

    [Fact]
    public async Task Owner_and_manager_manage_the_catalog_every_staff_member_reads_it_and_changes_are_audited()
    {
        var owner = await _factory.LoginOwnerAsync();
        var manager = await StaffAsync(StaffRoles.Manager);
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var tech = await StaffAsync(StaffRoles.Tech);
        var name = Unique("Counter-Strike 2");

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/games", Steam(name))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync("/api/games", Steam(name))).StatusCode);
        var created = await JsonAsync(await manager.PostAsJsonAsync("/api/games", Steam(name)));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(("steam", 730, "cs2", "CS"), (created.GetProperty("kind").GetString(), created.GetProperty("steamAppId").GetInt32(),
            created.GetProperty("processName").GetString(), created.GetProperty("initials").GetString()));
        Assert.Equal(GameTile.DefaultColor(name), created.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("exePath").ValueKind);

        foreach (var reader in new[] { cashier, tech })
            Assert.Contains((await reader.GetFromJsonAsync<JsonElement>("/api/games")).EnumerateArray(), g => g.GetProperty("id").GetString() == id);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PutAsJsonAsync($"/api/games/{id}", Steam(name))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.DeleteAsync($"/api/games/{id}")).StatusCode);

        // A duplicate name (any case) is refused
        var dup = await owner.PostAsJsonAsync("/api/games", Steam(name.ToUpperInvariant()));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("game_name_taken", await CodeAsync(dup));

        // Update: the audit lists the changed fields with the values before and after
        var changed = await JsonAsync(await owner.PutAsJsonAsync($"/api/games/{id}", new
        {
            name, category = "Шутери", kind = "exe", exePath = @"C:\Games\CS2\cs2.exe", arguments = "-novid -high", color = "#123ABC", sortOrder = 5
        }));
        Assert.Equal(("exe", "#123abc", JsonValueKind.Null), (changed.GetProperty("kind").GetString(), changed.GetProperty("color").GetString(),
            changed.GetProperty("steamAppId").ValueKind));
        var audit = (await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=game.updated")).EnumerateArray()
            .Single(a => a.GetProperty("targetId").GetString() == id);
        Assert.Equal($"Game '{name}' changed: kind, exePath, arguments, steamAppId, processName, color, sortOrder", audit.GetProperty("summary").GetString());
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=game.created")).EnumerateArray(),
            a => a.GetProperty("targetId").GetString() == id && a.GetProperty("summary").GetString()!.Contains("steam Steam 730, all zones"));

        // Delete without play history
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/games/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/games/{id}")).StatusCode);
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=game.deleted")).EnumerateArray(), a => a.GetProperty("targetId").GetString() == id);
    }

    [Theory]
    [InlineData("exe", "exePath", "cs2.exe", "game_bad_exe")]                  // relative
    [InlineData("exe", "exePath", @"C:\Games\..\Windows\cmd.exe", "game_bad_exe")]
    [InlineData("exe", "exePath", @"C:\Games\cs2.bat", "game_bad_exe")]
    [InlineData("exe", "exePath", "C:\\Games\\\"cs2.exe", "game_bad_exe")]
    [InlineData("exe", "exePath", @"\\server\share\game.exe", "game_bad_exe")]  // network share
    [InlineData("exe", "exePath", @"C:\Games\game.exe:stream", "game_bad_exe")]
    [InlineData("url", "launchUrl", "https://example.com/game", "game_bad_url")]
    [InlineData("url", "launchUrl", "file:///C:/Windows/System32/cmd.exe", "game_bad_url")]
    [InlineData("url", "launchUrl", "ms-settings:privacy", "game_bad_url")]
    [InlineData("url", "launchUrl", "steam://run/730 -console", "game_bad_url")]
    [InlineData("steam", "steamAppId", "0", "game_bad_steam_id")]
    [InlineData("other", "launchUrl", "steam://run/730", "game_bad_kind")]
    public async Task Launch_fields_are_checked(string kind, string field, string value, string code)
    {
        var manager = await StaffAsync(StaffRoles.Manager);
        var body = new Dictionary<string, object?> { ["name"] = Unique("Bad"), ["category"] = "Інше", ["kind"] = kind };
        body[field] = field == "steamAppId" ? int.Parse(value) : value;
        var res = await manager.PostAsJsonAsync("/api/games", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(code, await CodeAsync(res));
    }

    [Fact]
    public async Task Other_fields_are_checked_and_good_entries_of_every_kind_are_accepted()
    {
        var manager = await StaffAsync(StaffRoles.Manager);
        async Task<string?> Code(object body) => await CodeAsync(await manager.PostAsJsonAsync("/api/games", body));
        Assert.Equal("game_bad_name", await Code(new { name = " ", category = "Інше", kind = "steam", steamAppId = 570 }));
        Assert.Equal("game_bad_category", await Code(new { name = Unique("G"), category = "", kind = "steam", steamAppId = 570 }));
        Assert.Equal("game_bad_color", await Code(new { name = Unique("G"), category = "Інше", kind = "steam", steamAppId = 570, color = "red" }));
        Assert.Equal("game_bad_process", await Code(new { name = Unique("G"), category = "Інше", kind = "steam", steamAppId = 570, processName = @"C:\x\dota2.exe" }));
        Assert.Equal("game_bad_zone", await Code(new { name = Unique("G"), category = "Інше", kind = "steam", steamAppId = 570, allZones = false }));
        Assert.Equal("game_bad_zone", await Code(new { name = Unique("G"), category = "Інше", kind = "steam", steamAppId = 570, allZones = false, zoneIds = new[] { Guid.NewGuid() } }));
        Assert.Equal("game_bad_args", await Code(new { name = Unique("G"), category = "Інше", kind = "exe", exePath = @"C:\G\g.exe", arguments = "a\nb" }));
        Assert.Equal("game_bad_workdir", await Code(new { name = Unique("G"), category = "Інше", kind = "exe", exePath = @"C:\G\g.exe", workingDirectory = "G" }));

        var exe = await JsonAsync(await manager.PostAsJsonAsync("/api/games", new
        {
            name = Unique("Valorant"), category = "Шутери", kind = "exe", exePath = @"C:\Riot Games\Riot Client\RiotClientServices.exe",
            arguments = "--launch-product=valorant --launch-patchline=live", workingDirectory = @"%ProgramFiles%\Riot", processName = "VALORANT-Win64-Shipping"
        }));
        Assert.Equal("VALORANT-Win64-Shipping", exe.GetProperty("processName").GetString());
        var url = await JsonAsync(await manager.PostAsJsonAsync("/api/games", new
        {
            name = Unique("Fortnite"), category = "Королівська битва", kind = "url",
            launchUrl = "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true"
        }));
        Assert.Equal("url", url.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_pc_gets_the_active_entries_of_its_zone_with_an_etag()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zoneA = await ZoneAsync(owner);
        var zoneB = await ZoneAsync(owner);
        var everywhere = Unique("All zones game");
        var onlyA = Unique("Zone A game");
        var inactive = Unique("Inactive game");
        await JsonAsync(await owner.PostAsJsonAsync("/api/games", Steam(everywhere, 570)));
        await JsonAsync(await owner.PostAsJsonAsync("/api/games", Steam(onlyA, 571, allZones: false, zoneIds: new[] { zoneA })));
        await JsonAsync(await owner.PostAsJsonAsync("/api/games", Steam(inactive, 572, isActive: false)));
        var pcA = await TestPc.AddAsync(_factory, Guid.Parse(zoneA));
        var pcB = await TestPc.AddAsync(_factory, Guid.Parse(zoneB));
        var pending = await TestPc.AddAsync(_factory, Guid.Parse(zoneA), DeviceStatuses.Pending);

        async Task<(HttpResponseMessage Res, List<string> Names)> CatalogAsync(TestPc pc, string? etag = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/devices/{pc.Id}/games");
            req.Headers.Add(DeviceKeyAuthenticationHandler.HeaderName, pc.Key);
            if (etag != null) req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
            var res = await _factory.NewClient(csrfHeader: false).SendAsync(req);
            var names = res.StatusCode == HttpStatusCode.OK
                ? (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(g => g.GetProperty("name").GetString()!).ToList()
                : new List<string>();
            return (res, names);
        }

        var (resA, namesA) = await CatalogAsync(pcA);
        Assert.Contains(everywhere, namesA);
        Assert.Contains(onlyA, namesA);
        Assert.DoesNotContain(inactive, namesA);
        var (_, namesB) = await CatalogAsync(pcB);
        Assert.Contains(everywhere, namesB);
        Assert.DoesNotContain(onlyA, namesB);

        // The same catalog: 304 without a body; a change gives a new ETag
        var etag = resA.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NotModified, (await CatalogAsync(pcA, etag)).Res.StatusCode);
        await JsonAsync(await owner.PostAsJsonAsync("/api/games", Steam(Unique("New game"), 573)));
        var (after, _) = await CatalogAsync(pcA, etag);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etag, after.Headers.ETag!.Tag);

        // Only an approved PC, only its own catalog, never with a staff login
        Assert.Equal(HttpStatusCode.Forbidden, (await pending.GetAsync($"/api/devices/{pending.Id}/games")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pcB.GetAsync($"/api/devices/{pcA.Id}/games")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync($"/api/devices/{pcA.Id}/games")).StatusCode);
    }

    [Fact]
    public async Task The_lock_screen_reports_games_the_map_shows_the_current_game_and_analytics_the_top_games()
    {
        var owner = await _factory.LoginOwnerAsync();
        var zone = await ZoneAsync(owner);
        await JsonAsync(await owner.PostAsJsonAsync("/api/tariffs", new { name = Unique("T"), pricePerHour = 60, zoneId = zone }));
        var cs = await JsonAsync(await owner.PostAsJsonAsync("/api/games", Steam(Unique("CS2"), 730)));
        var dota = await JsonAsync(await owner.PostAsJsonAsync("/api/games", Steam(Unique("Dota 2"), 570)));
        var off = await JsonAsync(await owner.PostAsJsonAsync("/api/games", Steam(Unique("Old"), 574, isActive: false)));
        var pc = await TestPc.AddAsync(_factory, Guid.Parse(zone));
        var session = await JsonAsync(await owner.PostAsJsonAsync("/api/sessions", new { deviceId = pc.Id, durationMinutes = 60, paymentMethod = "cash" }));

        Task<HttpResponseMessage> Event(string type, JsonElement game) =>
            pc.PostAsync($"/api/devices/{pc.Id}/events", new { id = Guid.NewGuid(), type, occurredUtc = DateTimeOffset.UtcNow, gameId = game.GetProperty("id").GetString() });
        async Task<JsonElement> Device() =>
            (await owner.GetFromJsonAsync<JsonElement>("/api/devices")).EnumerateArray().Single(d => d.GetProperty("id").GetString() == pc.Id.ToString());

        Assert.Equal(HttpStatusCode.OK, (await Event("game_started", cs)).StatusCode);
        Assert.Equal(cs.GetProperty("name").GetString(), (await Device()).GetProperty("currentGame").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Event("game_started", cs)).StatusCode); // sent twice: one play
        Assert.Equal(HttpStatusCode.OK, (await Event("game_started", dota)).StatusCode); // switch
        Assert.Equal(dota.GetProperty("name").GetString(), (await Device()).GetProperty("currentGame").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Event("game_started", off)).StatusCode); // not in the catalog any more
        var unknown = await pc.PostAsync($"/api/devices/{pc.Id}/events", new { id = Guid.NewGuid(), type = "game_started", occurredUtc = DateTimeOffset.UtcNow, gameId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Plays of the session: CS2 (closed by the switch) and Dota 2 (open)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var plays = await db.GamePlays.Where(p => p.DeviceId == pc.Id).ToListAsync();
            Assert.Equal(2, plays.Count);
            Assert.All(plays, p => Assert.Equal(Guid.Parse(session.GetProperty("id").GetString()!), p.SessionId));
            Assert.Equal(GamePlayEndReasons.Switched, plays.Single(p => p.EndUtc != null).EndReason);
            // Analytics count the minutes; make the plays an hour old for clear numbers
            foreach (var p in plays) { p.StartUtc = p.StartUtc.AddMinutes(-60); if (p.EndUtc != null) p.EndUtc = p.StartUtc.AddMinutes(20); }
            await db.SaveChangesAsync();
        }
        var analytics = await owner.GetFromJsonAsync<JsonElement>("/api/analytics?period=7d");
        var top = analytics.GetProperty("topGames").EnumerateArray().ToList();
        var dotaRow = top.Single(g => g.GetProperty("name").GetString() == dota.GetProperty("name").GetString());
        var csRow = top.Single(g => g.GetProperty("name").GetString() == cs.GetProperty("name").GetString());
        Assert.True(Dec(dotaRow, "hours") >= 0.9m && Dec(csRow, "hours") is >= 0.3m and <= 0.4m, $"{Dec(dotaRow, "hours")} / {Dec(csRow, "hours")}");
        Assert.Equal(1, csRow.GetProperty("plays").GetInt32());

        // A game deleted with history is refused (take it off the catalog instead)
        var del = await owner.DeleteAsync($"/api/games/{cs.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Equal("game_has_history", await CodeAsync(del));

        // Stopped by the lock screen; the next game ends with the session
        Assert.Equal(HttpStatusCode.OK, (await Event("game_stopped", dota)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Device()).GetProperty("currentGame").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Event("game_started", cs)).StatusCode);
        await JsonAsync(await owner.PostAsJsonAsync($"/api/sessions/{session.GetProperty("id").GetString()}/end", new { }));
        Assert.Equal(JsonValueKind.Null, (await Device()).GetProperty("currentGame").ValueKind);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            // The background service (every 5 s) may have closed it already
            Assert.InRange(await GamePlays.CloseStaleAsync(db, DateTimeOffset.UtcNow), 0, 1);
            Assert.False(await db.GamePlays.AnyAsync(p => p.DeviceId == pc.Id && p.EndUtc == null));
            Assert.Equal(GamePlayEndReasons.SessionEnded, (await db.GamePlays.AsNoTracking().Where(p => p.DeviceId == pc.Id).ToListAsync())
                .OrderBy(p => p.StartUtc).Last().EndReason);
        }
        // The analytics CSV has the games
        var csv = await (await owner.GetAsync("/api/analytics/export.csv?period=7d&lang=en&sep=comma")).Content.ReadAsStringAsync();
        Assert.Contains("Game,Category,Plays,Hours,\"Share, %\"", csv);
    }
}
