using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

public sealed class PlayerPasswordTests : IClassFixture<CmsFactory>
{
    private readonly CmsFactory _factory;

    public PlayerPasswordTests(CmsFactory factory) => _factory = factory;

    /// <summary>The lock screen (Cms.Launcher) calls this without a cookie.</summary>
    private Task<HttpResponseMessage> PlayerLoginAsync(string username, string? password) =>
        _factory.NewClient(csrfHeader: false).PostAsJsonAsync("/api/users/login", new { username, password });

    private async Task<Guid> AddPlayerAsync(string username, string? storedHash)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var user = new UserEntity { Id = Guid.NewGuid(), Username = username, DisplayName = username, PasswordHash = storedHash };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<string?> StoredHashAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == id)).PasswordHash;
    }

    private static string LegacySha256(string password) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    [Fact]
    public async Task A_legacy_sha256_hash_is_replaced_by_a_salted_hash_at_the_next_login()
    {
        var legacy = LegacySha256("old-pin-7788");
        var id = await AddPlayerAsync("legacy-player", legacy);
        Assert.True(Passwords.IsLegacyHash(legacy));

        Assert.Equal(HttpStatusCode.Unauthorized, (await PlayerLoginAsync("legacy-player", "wrong")).StatusCode);
        Assert.Equal(legacy, await StoredHashAsync(id)); // a failed login changes nothing

        Assert.Equal(HttpStatusCode.OK, (await PlayerLoginAsync("legacy-player", "old-pin-7788")).StatusCode);
        var upgraded = await StoredHashAsync(id);
        Assert.NotEqual(legacy, upgraded);
        Assert.False(Passwords.IsLegacyHash(upgraded));
        Assert.Equal(PasswordCheck.Success, Passwords.Verify(upgraded, "old-pin-7788"));

        Assert.Equal(HttpStatusCode.OK, (await PlayerLoginAsync("legacy-player", "old-pin-7788")).StatusCode);
        Assert.Equal(upgraded, await StoredHashAsync(id));
    }

    [Fact]
    public async Task A_player_without_a_password_cannot_log_in_with_any_password()
    {
        var id = await AddPlayerAsync("no-password", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PlayerLoginAsync("no-password", "anything")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PlayerLoginAsync("no-password", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PlayerLoginAsync("no-password", "")).StatusCode);

        // Staff set a PIN; then the login works with exactly that PIN
        var owner = await _factory.LoginOwnerAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/users/{id}/password", new { password = "12" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/users/{id}/password", new { password = "4321" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlayerLoginAsync("no-password", "4321")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PlayerLoginAsync("no-password", "anything")).StatusCode);
    }

    [Fact]
    public async Task Registration_requires_a_password_and_stores_a_salted_hash()
    {
        var owner = await _factory.LoginOwnerAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/users/register", new { username = "nopass" })).StatusCode);

        var res = await owner.PostAsJsonAsync("/api/users/register", new { username = "NewPlayer", password = "pass-5566", displayName = "New" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var hash = await StoredHashAsync(id);
        Assert.StartsWith("AQAAAA", hash); // ASP.NET Core Identity v3 format
        Assert.Equal(HttpStatusCode.OK, (await PlayerLoginAsync("newplayer", "pass-5566")).StatusCode);
    }

    [Fact]
    public async Task Pin_guessing_is_locked_after_five_failures()
    {
        await AddPlayerAsync("pin-guess", Passwords.Hash("2580"));
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PlayerLoginAsync("pin-guess", $"000{i}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PlayerLoginAsync("pin-guess", "2580")).StatusCode);
    }
}
