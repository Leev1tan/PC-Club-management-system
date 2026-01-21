using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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
    // Use SQLite by default for easy testing
    builder.Services.AddDbContext<CmsDbContext>(options => 
        options.UseSqlite("Data Source=cms.db"));
}
else
{
    // Use PostgreSQL if configured
    builder.Services.AddDbContext<CmsDbContext>(options => 
        options.UseNpgsql(connectionString));
}

// Repositories
builder.Services.AddScoped<Cms.Server.Repositories.IDeviceRepository, Cms.Server.Repositories.EfDeviceRepository>();
builder.Services.AddScoped<Cms.Server.Repositories.ISessionRepository, Cms.Server.Repositories.EfSessionRepository>();

// UDP Discovery for LAN auto-discovery
builder.Services.AddHostedService<Cms.Server.DiscoveryService>();

var app = builder.Build();

// Auto-create database (EnsureCreated for SQLite, Migrate for PostgreSQL)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthorization();

app.MapControllers();

app.Run();
