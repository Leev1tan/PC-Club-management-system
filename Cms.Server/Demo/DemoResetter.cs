using Cms.Server.Billing;
using Cms.Server.Data;
using Cms.Server.Repositories;

namespace Cms.Server.Demo;

/// <summary>Wipes and re-seeds the demo club, then points the simulator at the new PCs.</summary>
public sealed class DemoResetter
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DemoOptions _options;
    private readonly DemoState _state;
    private readonly DemoSimulator _simulator;
    private readonly DeviceRuntimeState _runtime;
    private readonly ILogger<DemoResetter> _logger;
    private readonly ClubClock _clock;

    private readonly Cms.Server.Accounts.GameAccountVault? _vault;

    public DemoResetter(IServiceScopeFactory scopes, DemoOptions options, DemoState state,
        DemoSimulator simulator, DeviceRuntimeState runtime, ILogger<DemoResetter> logger, ClubClock clock,
        Cms.Server.Accounts.GameAccountVault? vault = null)
    {
        _vault = vault;
        _scopes = scopes;
        _options = options;
        _state = state;
        _simulator = simulator;
        _runtime = runtime;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Startup reset. Throws if the database holds data that demo mode did not create.</summary>
    public async Task ResetOnStartupAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        if (!await DemoSeeder.IsDemoOrEmptyAsync(db, ct))
        {
            throw new InvalidOperationException(
                "DEMO_MODE=true, but the database contains data that demo mode did not create " +
                "(real devices, zones, tariffs, players or sessions). Demo mode wipes the database on start " +
                "and periodically, so it refuses to run on this database. Use a separate, empty database for the demo.");
        }
        await ResetAsync(db, ct);
    }

    public async Task ResetAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var seed = await DemoSeeder.ResetAsync(db, now, Random.Shared, _clock.Zone, ct, _vault);
        _runtime.Clear();
        _simulator.Load(seed, now);
        _state.MarkReset(now, _options.ResetInterval);
        _logger.LogInformation("Demo club reset in {Ms} ms: {Count} simulated PCs, {Days} days of history; next reset {Next}",
            watch.ElapsedMilliseconds, seed.Pcs.Count, DemoSeeder.HistoryDays, _state.NextResetUtc?.ToString("u") ?? "on restart");
    }
}

/// <summary>Runs the simulator once per second and the periodic demo reset.</summary>
public sealed class DemoBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DemoState _state;
    private readonly DemoResetter _resetter;
    private readonly DemoSimulator _simulator;
    private readonly ILogger<DemoBackgroundService> _logger;

    public DemoBackgroundService(IServiceScopeFactory scopes, DemoState state, DemoResetter resetter,
        DemoSimulator simulator, ILogger<DemoBackgroundService> logger)
    {
        _scopes = scopes;
        _state = state;
        _resetter = resetter;
        _simulator = simulator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
                    var now = DateTimeOffset.UtcNow;
                    if (_state.NextResetUtc is { } next && now >= next)
                    {
                        await _resetter.ResetAsync(db, stoppingToken);
                        continue;
                    }
                    var repo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                    await _simulator.TickAsync(db, repo, now, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Demo simulator tick failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
