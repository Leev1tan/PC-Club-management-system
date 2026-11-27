using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;

namespace Cms.Server;

/// <summary>
/// Every 5 s: ends prepaid and package sessions whose time ran out and locks their PCs. A paused
/// session waits (its time stands still) unless its package cut-off (night pack 08:00) has passed.
/// It also closes the game plays of sessions that ended.
/// </summary>
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
        var clock = scope.ServiceProvider.GetRequiredService<ClubClock>();

        var closed = await new SessionService(db, devices, clock).CloseExpiredAsync(ct);
        if (closed > 0)
            _logger.LogInformation("Closed {Count} expired prepaid session(s)", closed);

        // Games of ended sessions: the play ends with the session (the lock screen kills the game too)
        db.ChangeTracker.Clear();
        await Cms.Server.Devices.GamePlays.CloseStaleAsync(db, clock.UtcNow, ct);
    }
}
