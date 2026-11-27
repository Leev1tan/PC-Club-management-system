using Cms.Server.Auth;
using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;

namespace Cms.Server.Events;

/// <summary>Every 15 s: events start at their start time and finish at their end (EventService.RunJobsAsync).</summary>
public sealed class EventJobService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EventJobService> _logger;

    public EventJobService(IServiceScopeFactory scopes, ILogger<EventJobService> logger)
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
                var service = new EventService(db, scope.ServiceProvider.GetRequiredService<IDeviceRepository>(),
                    scope.ServiceProvider.GetRequiredService<ClubClock>(), new AuditLogger(db, new HttpContextAccessor()));
                var result = await service.RunJobsAsync(stoppingToken);
                if (result.Started + result.Finished + result.Cancelled + result.Seats > 0)
                    _logger.LogInformation("Events: {Started} started, {Finished} finished, {Cancelled} cancelled (not started), {Seats} late session(s)",
                        result.Started, result.Finished, result.Cancelled, result.Seats);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Event jobs failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
