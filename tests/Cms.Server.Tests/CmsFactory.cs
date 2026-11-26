using System.Net.Http.Json;
using Cms.Server.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>
/// Runs the real server (Program.cs) in memory on its own SQLite file and data directory.
/// Settings are passed like environment variables.
/// </summary>
public class CmsFactory : WebApplicationFactory<Program>
{
    public const string OwnerUsername = "owner";
    public const string OwnerPassword = "owner-pass-123";

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "ccms-tests-" + Guid.NewGuid().ToString("N"));

    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Setup:OwnerUsername"] = OwnerUsername,
        ["Setup:OwnerPassword"] = OwnerPassword,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDirectory);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "sqlite");
        builder.UseSetting("Database:SqlitePath", Path.Combine(DataDirectory, "cms.db"));
        builder.UseSetting("Storage:DataDirectory", DataDirectory);
        builder.UseSetting("Discovery:Enabled", "false");
        builder.UseSetting("Auth:LoginRateLimitPerMinute", "10000");
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
    }

    /// <summary>A browser-like client: keeps cookies and sends the CSRF header like the admin UI.</summary>
    public HttpClient NewClient(bool csrfHeader = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        if (csrfHeader) client.DefaultRequestHeaders.Add(AuthConstants.CsrfHeader, "1");
        return client;
    }

    public async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = NewClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        return client;
    }

    public Task<HttpClient> LoginOwnerAsync() => LoginAsync(OwnerUsername, OwnerPassword);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, recursive: true); } catch { /* temp files */ }
    }
}

/// <summary>No initial owner: the server opens the one-time setup screen.</summary>
public sealed class SetupFactory : CmsFactory
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>();
}

public sealed class DemoFactory : CmsFactory
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["DEMO_MODE"] = "true",
        ["DEMO_RESET_HOURS"] = "0",
    };
}
