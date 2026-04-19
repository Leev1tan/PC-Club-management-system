using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Support running as Windows Service
builder.Host.UseWindowsService();

// Explicit URL binding (launchSettings.json only works with dotnet run)
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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Club Management API", Version = "v1" });
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
    });
});

// Database - Use SQLite for easy testing, PostgreSQL for production
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("localhost"))
{
    builder.Services.AddDbContext<CmsDbContext>(options => 
        options.UseSqlite("Data Source=cms.db"));
}
else
{
    builder.Services.AddDbContext<CmsDbContext>(options => 
        options.UseNpgsql(connectionString));
}

// Repositories
builder.Services.AddScoped<Cms.Server.Repositories.IDeviceRepository, Cms.Server.Repositories.EfDeviceRepository>();
builder.Services.AddScoped<Cms.Server.Repositories.ISessionRepository, Cms.Server.Repositories.EfSessionRepository>();

// UDP Discovery for LAN auto-discovery
builder.Services.AddHostedService<Cms.Server.DiscoveryService>();

var app = builder.Build();

// Auto-create/migrate database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
    db.Database.EnsureCreated();
}

// Swagger enabled in all environments
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Club Management API v1"));

app.UseCors();
app.UseAuthorization();

app.MapControllers();

// Health endpoint
app.MapGet("/health", () => Results.Ok(new 
{ 
    status = "healthy", 
    timestamp = DateTimeOffset.UtcNow,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"
}));

app.Run();
