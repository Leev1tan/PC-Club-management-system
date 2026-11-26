using Cms.Server;
using Cms.Server.Data;
using Cms.Server.Demo;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Support running as Windows Service (no-op on Linux / in containers)
builder.Host.UseWindowsService();

// Explicit URL binding (launchSettings.json only works with dotnet run).
// In containers set ASPNETCORE_URLS (the Docker image uses http://+:8080).
var urls = builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(urls))
{
    builder.WebHost.UseUrls(urls);
}
else if (!builder.Environment.IsDevelopment())
{
    // Default for production/service: listen on all interfaces
    builder.WebHost.UseUrls("http://0.0.0.0:5081");
}

var demo = DemoOptions.FromConfiguration(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Club Management API", Version = "v1" });
});

// CORS. The admin UI is normally served from the same origin as the API
// (Windows installer: wwwroot; containers: reverse proxy / ingress path split),
// so cross-origin access is only needed for development setups.
//   Cors:AllowedOrigins  - comma separated list (or JSON array) of allowed origins
//   Cors:AllowAnyOrigin  - legacy behaviour, default true; the Docker image sets it to false
var corsOrigins = ReadList(builder.Configuration, "Cors:AllowedOrigins");
var corsAllowAny = builder.Configuration.GetValue("Cors:AllowAnyOrigin", true);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsOrigins.Length > 0)
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
        else if (corsAllowAny)
            policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
        // else: no cross-origin access at all (same-origin only)
    });
});

// Database: PostgreSQL for production/containers, SQLite for single-PC installs and testing.
//   Database:Provider    - "postgres" or "sqlite"; when empty the legacy rule is used:
//                          no connection string, or one that contains "localhost" => SQLite
//   Database:SqlitePath  - SQLite file path (default: cms.db in the working directory)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var provider = (builder.Configuration["Database:Provider"] ?? string.Empty).Trim().ToLowerInvariant();
var useSqlite = provider switch
{
    "sqlite" => true,
    "postgres" or "postgresql" or "npgsql" => false,
    _ => string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("localhost")
};
if (useSqlite)
{
    var sqlitePath = builder.Configuration["Database:SqlitePath"];
    if (string.IsNullOrWhiteSpace(sqlitePath)) sqlitePath = "cms.db";
    builder.Services.AddDbContext<CmsDbContext>(options =>
        options.UseSqlite($"Data Source={sqlitePath}"));
}
else
{
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Database:Provider is postgres but ConnectionStrings:DefaultConnection is empty.");
    builder.Services.AddDbContext<CmsDbContext>(options =>
        options.UseNpgsql(connectionString));
}

// Repositories
builder.Services.AddSingleton<DeviceRuntimeState>();
builder.Services.AddScoped<Cms.Server.Repositories.IDeviceRepository, Cms.Server.Repositories.EfDeviceRepository>();
builder.Services.AddScoped<Cms.Server.Repositories.ISessionRepository, Cms.Server.Repositories.EfSessionRepository>();

// Background services
if (!demo.Enabled)
{
    // UDP Discovery for LAN auto-discovery (not useful for the public demo)
    builder.Services.AddHostedService<DiscoveryService>();
}
builder.Services.AddHostedService<SessionExpiryService>();

// Demo mode: seeded demo club + simulated PCs (DEMO_MODE=true)
builder.Services.AddDemoMode(demo);

var app = builder.Build();

// Create or migrate the database (PostgreSQL: EF Core migrations; SQLite: EnsureCreated)
await DatabaseInitializer.InitializeAsync(app.Services, app.Logger);

if (demo.Enabled)
{
    // Wipes and re-seeds the demo club. Refuses to touch a database that holds non-demo data.
    try
    {
        await app.Services.GetRequiredService<DemoResetter>().ResetOnStartupAsync();
    }
    catch (InvalidOperationException ex)
    {
        app.Logger.LogCritical("{Message}", ex.Message);
        return 1;
    }
}

// Swagger enabled in all environments
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Club Management API v1"));

app.UseCors();
app.UseDemoRestrictions(demo);
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();

app.MapControllers();
app.MapDemoEndpoints();

// Health endpoint (/api/health is the same, for setups that route only /api to the server)
IResult Health() => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
    demoMode = demo.Enabled
});
app.MapGet("/health", Health);
app.MapGet("/api/health", Health);

app.Run();
return 0;

static string[] ReadList(IConfiguration config, string key)
{
    var section = config.GetSection(key);
    var values = section.GetChildren().Select(c => c.Value).ToList();
    if (values.Count == 0) values.Add(section.Value);
    return values
        .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();
}

// Allows WebApplicationFactory-based tests to reference the entry point.
public partial class Program { }
