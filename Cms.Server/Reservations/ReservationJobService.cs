using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;

namespace Cms.Server.Reservations;

/// <summary>Every 15 s: no-shows, expired requests, the PC of an any-PC reservation at its hold (ReservationService.RunJobsAsync).</summary>
public sealed class ReservationJobService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReservationJobService> _logger;

    public ReservationJobService(IServiceScopeFactory scopes, ILogger<ReservationJobService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
                var service = new ReservationService(db, scope.ServiceProvider.GetRequiredService<IDeviceRepository>(),
                    scope.ServiceProvider.GetRequiredService<ClubClock>(), new AuditLogger(db, new HttpContextAccessor()));
                var result = await service.RunJobsAsync(stoppingToken);
                if (result.NoShows + result.Expired + result.Erased > 0)
                    _logger.LogInformation("Reservations: {NoShows} no-show(s), {Expired} expired request(s), personal data of {Erased} erased",
                        result.NoShows, result.Expired, result.Erased);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reservation jobs failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
