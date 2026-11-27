using Cms.Server;
using Cms.Server.Auth;
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
var paths = ServerPaths.Resolve(builder.Configuration, builder.Environment.ContentRootPath);

// A cash or card payment without an open shift answers 409 { code: "shift_required" }
builder.Services.AddControllers(o => o.Filters.Add<Cms.Server.Shifts.ShiftRequiredFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Club Management API", Version = "v1" });
});

// CORS: same origin only by default. The admin UI is served from the same origin as the API
// (Windows installer: wwwroot; containers: the admin-ui proxy or an ingress path split).
//   Cors:AllowedOrigins  - comma separated list (or JSON array) of extra allowed origins, for example a
//                          development UI on http://localhost:3000; cookies are allowed for these origins
var corsOrigins = ReadList(builder.Configuration, "Cors:AllowedOrigins");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsOrigins.Length > 0)
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        // else: no cross-origin access at all (same-origin only)
    });
});

// Staff login (cookie), roles and policies, login rate limit, forwarded headers
builder.AddStaffAuth(paths, demo.Enabled);

// Database: PostgreSQL for production/containers, SQLite for single-PC installs and testing.
//   Database:Provider    - "postgres" or "sqlite"; when empty the legacy rule is used:
//                          no connection string, or one that contains "localhost" => SQLite
//   Database:SqlitePath  - SQLite file; default <data directory>/cms.db (Windows: C:\ProgramData\PCClub\cms.db).
//                          A relative path is resolved against the data directory, never the working directory.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var provider = (builder.Configuration["Database:Provider"] ?? string.Empty).Trim().ToLowerInvariant();
var useSqlite = provider switch
{
    "sqlite" => true,
    "postgres" or "postgresql" or "npgsql" => false,
    _ => string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("localhost")
};
string? sqliteCopiedFrom = null;
string? sqlitePath = null;
if (useSqlite)
{
    (sqlitePath, sqliteCopiedFrom) = paths.ResolveSqlitePath(builder.Configuration);
    var sqliteConnection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sqlitePath }.ToString();
    builder.Services.AddDbContext<CmsDbContext>(options => options.UseSqlite(sqliteConnection));
}
else
{
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Database:Provider is postgres but ConnectionStrings:DefaultConnection is empty.");
    builder.Services.AddDbContext<CmsDbContext>(options =>
        options.UseNpgsql(connectionString));
}

// Cash and card payments need an open shift (Shifts:RequireOpenShift, default true); used by CmsDbContext
builder.Services.AddSingleton(ShiftOptions.FromConfiguration(builder.Configuration));

// Club time zone for tariff rules and packages (Club:TimeZone, default Europe/Kyiv)
builder.Services.AddSingleton(sp => Cms.Server.Billing.ClubClock.FromConfiguration(
    builder.Configuration, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Cms.Server.Billing")));

// Repositories
builder.Services.AddSingleton<DeviceRuntimeState>();
builder.Services.AddSingleton<Cms.Server.Devices.PlayerLoginLimiter>();
builder.Services.AddSingleton<Cms.Server.Reservations.BookingPhoneLimiter>();
// Wake-on-LAN: UDP to Power:WakeBroadcast / Power:WakePort; the demo simulates it (no network traffic)
builder.Services.AddSingleton(Cms.Server.Devices.PowerOptions.FromConfiguration(builder.Configuration));
if (demo.Enabled)
    builder.Services.AddSingleton<Cms.Server.Devices.IWakeOnLanSender, Cms.Server.Demo.DemoWakeOnLanSender>();
else
    builder.Services.AddSingleton<Cms.Server.Devices.IWakeOnLanSender, Cms.Server.Devices.UdpWakeOnLanSender>();
builder.Services.AddScoped<Cms.Server.Repositories.IDeviceRepository, Cms.Server.Repositories.EfDeviceRepository>();
builder.Services.AddScoped<Cms.Server.Repositories.ISessionRepository, Cms.Server.Repositories.EfSessionRepository>();

// Background services
if (!demo.Enabled)
{
    // UDP Discovery for LAN auto-discovery (not useful for the public demo)
    builder.Services.AddHostedService<DiscoveryService>();
}
builder.Services.AddHostedService<SessionExpiryService>();
// Reservations: no-shows, expired requests, the PC of an any-PC reservation at its hold
builder.Services.AddHostedService<Cms.Server.Reservations.ReservationJobService>();
// Events: start at the start time, finish at the end (entry fees kept), cancel when never started
builder.Services.AddHostedService<Cms.Server.Events.EventJobService>();

// Retention: heartbeat samples and finished commands are deleted after a configurable time
builder.Services.AddSingleton(RetentionOptions.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<RetentionService>();

// Demo mode: seeded demo club + simulated PCs (DEMO_MODE=true)
builder.Services.AddDemoMode(demo);

var app = builder.Build();

if (builder.Configuration.GetValue("Cors:AllowAnyOrigin", false))
    app.Logger.LogWarning("Cors:AllowAnyOrigin is no longer supported and is ignored; use Cors:AllowedOrigins.");
app.Logger.LogInformation("Data directory: {DataDirectory}", paths.DataDirectory);
if (app.Services.GetRequiredService<Cms.Server.Devices.PowerOptions>().Error is { } powerError)
    app.Logger.LogError("Wake-on-LAN is off: {Error}", powerError);
if (sqlitePath != null)
    app.Logger.LogInformation("SQLite database: {Path}", sqlitePath);
if (sqliteCopiedFrom != null)
    app.Logger.LogWarning("Copied the SQLite database of an earlier version from {Old} to {New}. " +
                          "The old file is kept; delete it after a check.", sqliteCopiedFrom, sqlitePath);

// Create or migrate the database (PostgreSQL: EF Core migrations; SQLite: EnsureCreated + additive upgrade)
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
else
{
    // First start: initial owner from the environment, or a one-time setup code for the setup screen
    await StaffBootstrapper.RunAsync(app.Services, builder.Configuration, app.Logger);
}

if (builder.Configuration.GetValue("ReverseProxy:TrustForwardedHeaders", false))
    app.UseForwardedHeaders();

// Swagger enabled in all environments
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Club Management API v1"));

app.UseCors();
app.UseDemoRestrictions(demo);
// The public booking page takes small JSON bodies only: over 2 KB is refused (413) before the JSON is read; a body
// without a length (chunked) is read up to the limit (Kestrel also enforces [RequestSizeLimit]; this covers every host)
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (HttpMethods.IsPost(request.Method) && request.Path.StartsWithSegments("/api/public/booking"))
    {
        const int max = Cms.Server.Controllers.PublicBookingController.MaxBodyBytes;
        if (request.ContentLength > max)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (request.ContentLength == null)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[512];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            request.Body = buffer;
            request.ContentLength = buffer.Length;
        }
    }
    await next(context);
});
// The public booking page: /book is the static book.html (Windows install: wwwroot; the admin-ui container does the same)
app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/book", StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals("/book/", StringComparison.OrdinalIgnoreCase))
        context.Request.Path = "/book.html";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCsrfProtection(corsOrigins);
app.UseAuthentication();
app.UseRateLimiter();
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
app.MapGet("/health", Health).AllowAnonymous();
app.MapGet("/api/health", Health).AllowAnonymous();

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
