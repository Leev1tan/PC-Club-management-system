using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server;

/// <summary>
/// Retention settings (appsettings "Retention" section or Retention__* environment variables).
///   Retention:HeartbeatDays          (7)   - keep heartbeat samples this many days
///   Retention:HeartbeatSampleMinutes (5)   - store at most one heartbeat sample per PC in this time;
///                                            every heartbeat only updates Devices.LastSeenUtc
///   Retention:CommandDays            (30)  - keep finished commands (done/failed/ignored/expired)
///   Retention:CommandExpireMinutes   (60)  - a command that the PC did not take in this time is
///                                            marked "expired" and never runs (an old "unlock" must
///                                            not run when the PC comes back days later)
///   Retention:IntervalMinutes        (60)  - how often the clean-up runs
/// </summary>
public sealed class RetentionOptions
{
    public int HeartbeatDays { get; init; } = 7;
    public TimeSpan HeartbeatSampleInterval { get; init; } = TimeSpan.FromMinutes(5);
    public int CommandDays { get; init; } = 30;
    public TimeSpan CommandExpireAfter { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(60);

    public static RetentionOptions FromConfiguration(IConfiguration config) => new()
    {
        HeartbeatDays = Math.Max(1, config.GetValue("Retention:HeartbeatDays", 7)),
        HeartbeatSampleInterval = TimeSpan.FromMinutes(Math.Max(0, config.GetValue("Retention:HeartbeatSampleMinutes", 5.0))),
        CommandDays = Math.Max(1, config.GetValue("Retention:CommandDays", 30)),
        CommandExpireAfter = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Retention:CommandExpireMinutes", 60.0))),
        Interval = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Retention:IntervalMinutes", 60.0))),
    };
}

public sealed record RetentionResult(int HeartbeatsDeleted, int CommandsExpired, int CommandsDeleted);

/// <summary>Deletes old heartbeat samples and finished commands, expires stale commands.</summary>
public sealed class RetentionService : BackgroundService
{
    private const int BatchSize = 5000;
    private readonly IServiceScopeFactory _scopes;
    private readonly RetentionOptions _options;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IServiceScopeFactory scopes, RetentionOptions options, ILogger<RetentionService> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // let the server start first
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
                    var result = await RunOnceAsync(db, _options, DateTimeOffset.UtcNow, stoppingToken);
                    if (result != new RetentionResult(0, 0, 0))
                        _logger.LogInformation("Retention: deleted {Heartbeats} heartbeat sample(s), expired {Expired} and deleted {Commands} command(s)",
                            result.HeartbeatsDeleted, result.CommandsExpired, result.CommandsDeleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Retention clean-up failed");
                }
                await Task.Delay(_options.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Plain SQL that works on PostgreSQL and SQLite (EF Core cannot translate DateTimeOffset
    /// comparisons for SQLite; its UTC text values sort correctly). Deletes run in batches, so a
    /// large table of an earlier version does not lock the database for a long time.
    /// </summary>
    public static async Task<RetentionResult> RunOnceAsync(CmsDbContext db, RetentionOptions options, DateTimeOffset now, CancellationToken ct = default)
    {
        var heartbeatCutoff = now.AddDays(-options.HeartbeatDays);
        var commandCutoff = now.AddDays(-options.CommandDays);
        var expireCutoff = now - options.CommandExpireAfter;

        var heartbeats = await DeleteInBatchesAsync(db,
            $"DELETE FROM \"Heartbeats\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"Heartbeats\" WHERE \"CreatedUtc\" < {{0}} LIMIT {BatchSize})",
            heartbeatCutoff, ct);

        var expired = await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Commands\" SET \"Status\" = 'expired', \"Result\" = 'Not delivered in time' " +
            "WHERE \"Status\" IN ('pending', 'delivered') AND \"CreatedUtc\" < {0}", new object[] { expireCutoff }, ct);

        var commands = await DeleteInBatchesAsync(db,
            $"DELETE FROM \"Commands\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"Commands\" WHERE \"Status\" IN ('done', 'failed', 'ignored', 'expired') AND \"CreatedUtc\" < {{0}} LIMIT {BatchSize})",
            commandCutoff, ct);

        return new RetentionResult(heartbeats, expired, commands);
    }

    private static async Task<int> DeleteInBatchesAsync(CmsDbContext db, string sql, DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var deleted = await db.Database.ExecuteSqlRawAsync(sql, new object[] { cutoff }, ct);
            total += deleted;
            if (deleted < BatchSize) return total;
        }
    }
}
