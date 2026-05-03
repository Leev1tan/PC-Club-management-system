using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server;

public sealed class SessionExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionExpiryService> _logger;

    public SessionExpiryService(IServiceScopeFactory scopeFactory, ILogger<SessionExpiryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseExpiredPrepaidSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to close expired sessions");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task CloseExpiredPrepaidSessionsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

        var now = DateTimeOffset.UtcNow;
        var active = await db.Sessions
            .Where(s => s.Status == "active" && s.IsPrepaid && s.EndUtc.HasValue)
            .ToListAsync(ct);

        var expired = active.Where(s => s.EndUtc!.Value <= now).ToList();
        if (expired.Count == 0) return;

        foreach (var session in expired)
        {
            session.Status = "ended";
            session.EndUtc = session.EndUtc ?? now;
        }

        await db.SaveChangesAsync(ct);

        foreach (var session in expired)
        {
            devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("session_set", new { endUtc = now }));
            devices.EnqueueCommand(session.DeviceId, new EnqueueCommandRequest("lock", null));
        }

        _logger.LogInformation("Closed {Count} expired prepaid session(s)", expired.Count);
    }
}
