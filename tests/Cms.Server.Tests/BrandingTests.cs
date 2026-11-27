using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cms.Server.Auth;
using Cms.Server.Branding;
using Cms.Server.Data;
using Cms.Server.Demo;
using Cms.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>Test images built byte by byte: a structurally valid PNG (CRCs, IHDR, IDAT, IEND) and a minimal JPEG (SOI, SOF0, EOI).</summary>
public static class TestImages
{
    public static byte[] Png(int width, int height)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        Chunk(ms, "IHDR", ihdr);
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + Math.Min(width, 64) * 4];
            for (var y = 0; y < Math.Min(height, 64); y++) z.Write(row);
        }
        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    public static byte[] Jpeg(int width, int height)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8 });
        ms.Write(new byte[] { 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        ms.Write(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
            1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
        ms.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x0C, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0, 0x12, 0x34, 0x56 });
        ms.Write(new byte[] { 0xFF, 0xD9 });
        return ms.ToArray();
    }

    public static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(body));
        s.Write(len);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Club theme, accent and logo (themes phase): Owner only, a public read for every page and the PCs, strict logo checks.</summary>
public sealed class BrandingTests : ApiTestBase, IClassFixture<CmsFactory>
{
    public BrandingTests(CmsFactory factory) : base(factory) { }

    private async Task<HttpClient> OwnerWithDefaultsAsync()
    {
        var owner = await _factory.LoginOwnerAsync();
        await JsonAsync(await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "cyber", accent = (string?)null }));
        await JsonAsync(await owner.DeleteAsync("/api/settings/logo"));
        return owner;
    }

    private static HttpContent Body(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private async Task<JsonElement> PublicThemeAsync()
    {
        var res = await _factory.NewClient(csrfHeader: false).GetAsync("/api/public/theme");
        return await JsonAsync(res);
    }

    [Fact]
    public async Task Owner_sets_theme_and_everyone_sees_it()
    {
        var owner = await OwnerWithDefaultsAsync();
        var pc = await TestPc.AddAsync(_factory);
        var hb = await pc.HeartbeatAsync();
        Assert.Equal("cyber", hb.GetProperty("client").GetProperty("theme").GetString());

        var set = await JsonAsync(await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "pro-dark", accent = (string?)null }));
        Assert.Equal("pro-dark", set.GetProperty("theme").GetString());
        var view = await PublicThemeAsync();
        Assert.Equal("pro-dark", view.GetProperty("theme").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("accent").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("logoSha256").ValueKind);

        var client = (await pc.HeartbeatAsync()).GetProperty("client");
        Assert.Equal("pro-dark", client.GetProperty("theme").GetString());
        Assert.Equal(JsonValueKind.Null, client.GetProperty("accent").ValueKind);
        Assert.Equal(JsonValueKind.Null, client.GetProperty("logoSha256").ValueKind);

        // An accent that fits both modes: stored in upper case, sent to the PCs
        set = await JsonAsync(await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "PRO-DARK", accent = "#e0457b" }));
        Assert.Equal("#E0457B", set.GetProperty("accent").GetString());
        Assert.Equal("#E0457B", (await pc.HeartbeatAsync()).GetProperty("client").GetProperty("accent").GetString());
        // An empty accent means the accent of the theme
        set = await JsonAsync(await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "pro-dark", accent = "" }));
        Assert.Equal(JsonValueKind.Null, set.GetProperty("accent").ValueKind);
    }

    [Fact]
    public async Task Cashier_gets_403_and_anonymous_401()
    {
        await OwnerWithDefaultsAsync();
        var cashier = await StaffAsync(StaffRoles.Cashier);
        var manager = await StaffAsync(StaffRoles.Manager);
        var anonymous = _factory.NewClient();
        var png = TestImages.Png(32, 32);
        foreach (var (client, status) in new[] { (cashier, HttpStatusCode.Forbidden), (manager, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
        {
            Assert.Equal(status, (await client.PutAsJsonAsync("/api/settings/theme", new { theme = "arcade" })).StatusCode);
            Assert.Equal(status, (await client.PutAsync("/api/settings/logo", Body(png, "image/png"))).StatusCode);
            Assert.Equal(status, (await client.DeleteAsync("/api/settings/logo")).StatusCode);
        }
        // Everybody may read the theme (the login screen and /book need it)
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/public/theme")).StatusCode);
        Assert.Equal("cyber", (await PublicThemeAsync()).GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Unknown_theme_is_400()
    {
        var owner = await OwnerWithDefaultsAsync();
        var res = await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "neon", accent = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("bad_theme", await CodeAsync(res));
        res = await owner.PutAsJsonAsync("/api/settings/theme", new { accent = (string?)null });
        Assert.Equal("bad_theme", await CodeAsync(res));
    }

    [Theory]
    [InlineData("#12")]
    [InlineData("blue")]
    [InlineData("#GGGGGG")]
    [InlineData("#E0457B; background:url(x)")]
    public async Task Bad_accent_format_is_400(string accent)
    {
        var owner = await OwnerWithDefaultsAsync();
        var res = await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "light", accent });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("bad_accent", await CodeAsync(res));
        Assert.Equal("cyber", (await PublicThemeAsync()).GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Accent_failing_one_mode_is_refused()
    {
        var owner = await OwnerWithDefaultsAsync();
        // Bright cyan: 13.7:1 on the dark surface of Cyber, 1.3:1 on its light surface
        Assert.True(ColorContrast.Ratio("#00F0FF", ClubThemes.SurfaceHex("cyber", "dark")) >= 3);
        Assert.True(ColorContrast.Ratio("#00F0FF", ClubThemes.SurfaceHex("cyber", "light")) < 3);
        var res = await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "cyber", accent = "#00F0FF" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("accent_low_contrast", body.GetProperty("code").GetString());
        Assert.Equal("light", body.GetProperty("mode").GetString());
        Assert.True(body.GetProperty("ratio").GetDouble() < 3);
        // Nothing changed
        var view = await PublicThemeAsync();
        Assert.Equal(("cyber", JsonValueKind.Null), (view.GetProperty("theme").GetString(), view.GetProperty("accent").ValueKind));
    }

    [Fact]
    public async Task Stored_unknown_theme_reads_as_cyber()
    {
        await OwnerWithDefaultsAsync();
        var pc = await TestPc.AddAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            foreach (var (key, value) in new[] { (ClubSettingKeys.Theme, "neon"), (ClubSettingKeys.Accent, "javascript:alert(1)") })
            {
                var row = await db.ClubSettings.FindAsync(key);
                if (row == null) db.ClubSettings.Add(new ClubSettingEntity { Key = key, Value = value });
                else row.Value = value;
            }
            await db.SaveChangesAsync();
        }
        try
        {
            var view = await PublicThemeAsync();
            Assert.Equal("cyber", view.GetProperty("theme").GetString());
            Assert.Equal(JsonValueKind.Null, view.GetProperty("accent").ValueKind);
            var client = (await pc.HeartbeatAsync()).GetProperty("client");
            Assert.Equal("cyber", client.GetProperty("theme").GetString());
            Assert.Equal(JsonValueKind.Null, client.GetProperty("accent").ValueKind);
        }
        finally
        {
            await OwnerWithDefaultsAsync();
        }
    }

    [Fact]
    public async Task Png_and_jpeg_logos_are_accepted_by_bytes()
    {
        var owner = await OwnerWithDefaultsAsync();
        var pc = await TestPc.AddAsync(_factory);
        // JPEG bytes declared as PNG (and named .png): stored and served as JPEG
        var jpeg = TestImages.Jpeg(300, 120);
        var view = await JsonAsync(await owner.PutAsync("/api/settings/logo", Body(jpeg, "image/png")));
        Assert.Equal(TestImages.Sha256Hex(jpeg), view.GetProperty("logoSha256").GetString());
        Assert.Equal(TestImages.Sha256Hex(jpeg), (await pc.HeartbeatAsync()).GetProperty("client").GetProperty("logoSha256").GetString());
        Assert.Equal(TestImages.Sha256Hex(jpeg), (await PublicThemeAsync()).GetProperty("logoSha256").GetString());

        var res = await _factory.NewClient(csrfHeader: false).GetAsync("/api/public/theme/logo");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(res.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("public, max-age=300", res.Headers.CacheControl?.ToString());
        Assert.Equal(jpeg, await res.Content.ReadAsByteArrayAsync());

        var png = TestImages.Png(64, 48);
        view = await JsonAsync(await owner.PutAsync("/api/settings/logo", Body(png, "application/octet-stream")));
        Assert.Equal(TestImages.Sha256Hex(png), view.GetProperty("logoSha256").GetString());
        res = await _factory.NewClient(csrfHeader: false).GetAsync("/api/public/theme/logo");
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await res.Content.ReadAsByteArrayAsync());

        // Removal: no logo, 404, audited
        view = await JsonAsync(await owner.DeleteAsync("/api/settings/logo"));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("logoSha256").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.NewClient(csrfHeader: false).GetAsync("/api/public/theme/logo")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await pc.HeartbeatAsync()).GetProperty("client").GetProperty("logoSha256").ValueKind);
        var removed = await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=settings.logo_removed");
        Assert.NotEmpty(removed.EnumerateArray());
    }

    public static TheoryData<string, byte[]> NotImages => new()
    {
        { "svg", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>") },
        { "gif", Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00\x00\x00\x00;") },
        { "webp", Encoding.ASCII.GetBytes("RIFF\x1a\x00\x00\x00WEBPVP8 \x0e\x00\x00\x00") },
        { "text", Encoding.ASCII.GetBytes("hello") },
        { "empty", Array.Empty<byte>() },
        // A PNG signature in front of other content
        { "png-then-html", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>")).ToArray() },
        // A valid PNG with something after IEND
        { "png-trailer", TestImages.Png(8, 8).Concat(Encoding.ASCII.GetBytes("<script>")).ToArray() },
        // A JPEG start without the end marker
        { "jpeg-cut", TestImages.Jpeg(8, 8)[..^2] },
    };

    [Theory]
    [MemberData(nameof(NotImages))]
    public async Task Svg_gif_webp_and_text_are_refused(string name, byte[] bytes)
    {
        var owner = await OwnerWithDefaultsAsync();
        var res = await owner.PutAsync("/api/settings/logo", Body(bytes, name == "svg" ? "image/png" : "image/jpeg"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("logo_type", await CodeAsync(res));
        Assert.Equal(JsonValueKind.Null, (await PublicThemeAsync()).GetProperty("logoSha256").ValueKind);
    }

    [Fact]
    public async Task Logo_over_512_KB_is_refused()
    {
        var owner = await OwnerWithDefaultsAsync();
        var png = TestImages.Png(16, 16);
        var big = new byte[LogoImage.MaxBytes + 1];
        png.CopyTo(big, 0);
        var res = await owner.PutAsync("/api/settings/logo", Body(big, "image/png"));
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, res.StatusCode.ToString());
        if (res.StatusCode == HttpStatusCode.BadRequest) Assert.Equal("logo_size", await CodeAsync(res));

        // Without a Content-Length (chunked): the server stops reading after the limit
        var chunked = new StreamContent(new MemoryStream(big));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/settings/logo") { Content = chunked };
        req.Headers.TransferEncodingChunked = true;
        res = await owner.SendAsync(req);
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, res.StatusCode.ToString());
        Assert.Equal(JsonValueKind.Null, (await PublicThemeAsync()).GetProperty("logoSha256").ValueKind);
        Assert.Equal(512 * 1024, LogoImage.MaxBytes);
    }

    [Fact]
    public async Task Logo_over_1024_px_is_refused()
    {
        var owner = await OwnerWithDefaultsAsync();
        foreach (var bytes in new[] { TestImages.Png(20000, 20000), TestImages.Png(1025, 10), TestImages.Jpeg(10, 4000) })
        {
            Assert.True(bytes.Length < 2048);
            var res = await owner.PutAsync("/api/settings/logo", Body(bytes, "image/png"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal("logo_dimensions", await CodeAsync(res));
        }
        // The limit itself is fine
        await JsonAsync(await owner.PutAsync("/api/settings/logo", Body(TestImages.Png(1024, 1024), "image/png")));
        await OwnerWithDefaultsAsync();
    }

    [Fact]
    public async Task Audit_has_hash_prefix_and_size_only()
    {
        var owner = await OwnerWithDefaultsAsync();
        var png = TestImages.Png(200, 100);
        await JsonAsync(await owner.PutAsync("/api/settings/logo", Body(png, "image/png")));
        var audit = await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=settings.logo_changed");
        var entry = audit.EnumerateArray().First();
        var text = entry.GetRawText();
        var sha = TestImages.Sha256Hex(png);
        Assert.Contains(sha[..12], text);
        Assert.Contains(png.Length.ToString(), text);
        Assert.Contains("image/png", text);
        Assert.DoesNotContain(sha, text);
        Assert.DoesNotContain(Convert.ToBase64String(png)[..16], text);
        Assert.Equal("Club logo changed: image/png, 200 x 100 px, " + png.Length + " bytes, SHA-256 " + sha[..12], entry.GetProperty("summary").GetString());
        await OwnerWithDefaultsAsync();
    }

    [Fact]
    public async Task Theme_change_is_audited_with_old_and_new()
    {
        var owner = await OwnerWithDefaultsAsync();
        await JsonAsync(await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "arcade", accent = "#E0457B" }));
        // The same values again: no second row
        await JsonAsync(await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "arcade", accent = "#E0457B" }));
        var audit = await owner.GetFromJsonAsync<JsonElement>("/api/audit?action=settings.theme_changed");
        var rows = audit.EnumerateArray().Select(e => e.GetProperty("summary").GetString()).ToList();
        var entry = Assert.Single(rows, s => s!.Contains("arcade"));
        Assert.Equal("Club theme changed: cyber -> arcade; accent: theme default -> #E0457B", entry);
        await OwnerWithDefaultsAsync();
    }

    [Fact]
    public void Logo_inspection_reads_the_header_and_checks_the_structure()
    {
        Assert.Equal(new LogoCheck(true, "image/png", 640, 480, null), LogoImage.Inspect(TestImages.Png(640, 480)));
        Assert.Equal(new LogoCheck(true, "image/jpeg", 300, 120, null), LogoImage.Inspect(TestImages.Jpeg(300, 120)));
        Assert.Equal("dimensions", LogoImage.Inspect(TestImages.Png(0, 10)).Error);
        Assert.Equal("dimensions", LogoImage.Inspect(TestImages.Jpeg(10, 0)).Error);
        // A changed byte in the IHDR breaks its CRC
        var png = TestImages.Png(10, 10);
        png[20] ^= 1;
        Assert.Equal("type", LogoImage.Inspect(png).Error);
        // A chunk length that points past the end
        png = TestImages.Png(10, 10);
        png[33] = 0x7F;
        Assert.Equal("type", LogoImage.Inspect(png).Error);
        Assert.Equal("size", LogoImage.Inspect(new byte[LogoImage.MaxBytes + 1]).Error);
    }
}

public sealed class DemoBrandingTests : IClassFixture<DemoFactory>
{
    private readonly DemoFactory _factory;

    public DemoBrandingTests(DemoFactory factory) => _factory = factory;

    [Fact]
    public async Task Demo_reset_restores_cyber_without_logo()
    {
        var owner = await _factory.LoginAsync("demo-owner", "demo");
        // The demo owner may switch the theme; a logo upload is closed in the public demo
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/settings/theme", new { theme = "arcade", accent = "#E0457B" })).StatusCode);
        var png = new ByteArrayContent(TestImages.Png(16, 16));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync("/api/settings/logo", png)).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
            var bytes = TestImages.Png(16, 16);
            await ClubBranding.SaveLogoAsync(db, bytes, LogoImage.Inspect(bytes), "test", DateTimeOffset.UtcNow);
        }
        var anonymous = _factory.NewClient(csrfHeader: false);
        var before = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/theme");
        Assert.Equal("arcade", before.GetProperty("theme").GetString());
        Assert.NotEqual(JsonValueKind.Null, before.GetProperty("logoSha256").ValueKind);

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DemoResetter>().ResetAsync(scope.ServiceProvider.GetRequiredService<CmsDbContext>());

        var after = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/theme");
        Assert.Equal("cyber", after.GetProperty("theme").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("accent").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("logoSha256").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/public/theme/logo")).StatusCode);
    }
}

/// <summary>The logo table: the PostgreSQL migration keeps phase 8 data; the SQLite upgrade adds it.</summary>
public sealed class BrandingSchemaTests : IDisposable
{
    private readonly Lazy<PostgresDb> _db = new(() => new PostgresDb(migrate: false));
    private readonly TestDb _sqlite = new();

    public void Dispose()
    {
        if (_db.IsValueCreated) _db.Value.Dispose();
        _sqlite.Dispose();
    }

    [PostgresFact]
    public async Task The_migration_keeps_phase_8_data_and_stores_the_logo_on_postgres()
    {
        await using var db = _db.Value.NewContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261127105013_GameAccountPool"); // last migration of phase 8
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ClubSettings\" (\"Key\", \"Value\", \"UpdatedUtc\") VALUES ('club.language', 'en', now())");
        await migrator.MigrateAsync();

        Assert.Equal("en", (await db.ClubSettings.AsNoTracking().SingleAsync(s => s.Key == "club.language")).Value);
        Assert.Equal(new ThemeView("cyber", null, null), await ClubBranding.LoadAsync(db));
        var jpeg = TestImages.Jpeg(120, 80);
        await ClubBranding.SaveLogoAsync(db, jpeg, LogoImage.Inspect(jpeg), "owner", DateTimeOffset.UtcNow);
        var png = TestImages.Png(100, 50);
        await ClubBranding.SaveLogoAsync(db, png, LogoImage.Inspect(png), "owner", DateTimeOffset.UtcNow); // replaces the row
        db.ChangeTracker.Clear();
        var logo = await db.ClubLogos.AsNoTracking().SingleAsync();
        Assert.Equal((1, "image/png", 100, 50), (logo.Id, logo.ContentType, logo.Width, logo.Height));
        Assert.Equal(png, logo.Data);
        Assert.Equal(TestImages.Sha256Hex(png), (await ClubBranding.LoadAsync(db)).LogoSha256);
    }

    [Fact]
    public async Task A_phase_8_sqlite_database_gets_the_logo_table()
    {
        await using (var db = _sqlite.NewContext())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ClubLogos\";");
        await using (var db = _sqlite.NewContext())
        {
            Assert.Contains("table ClubLogos", await SqliteSchemaUpgrader.UpgradeAsync(db));
            Assert.Empty(await SqliteSchemaUpgrader.UpgradeAsync(db));
            Assert.Equal(new ThemeView("cyber", null, null), await ClubBranding.LoadAsync(db));
        }
    }
}
