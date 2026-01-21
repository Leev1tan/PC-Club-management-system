using System.Net.Http.Json;

namespace Cms.Agent.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    private Guid _deviceId;
    private string? _deviceKey;
    private DateTimeOffset? _sessionEndUtc;

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredUrl = _config["Server:BaseUrl"];
        var baseUrl = await ResolveServerUrlAsync(configuredUrl, stoppingToken);
        
        _logger.LogInformation("Agent starting, server URL: {BaseUrl}", baseUrl);
        
        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(baseUrl);

        // Initial registration with retry - won't crash if server is down
        await EnsureRegisteredWithRetryAsync(http, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hbOk = await SendHeartbeatAsync(http, stoppingToken);
                if (!hbOk)
                {
                    _logger.LogWarning("Heartbeat unauthorized; re-registering");
                    _deviceId = Guid.Empty;
                    _deviceKey = null;
                    await EnsureRegisteredWithRetryAsync(http, stoppingToken);
                }
                await PollAndExecuteAsync(http, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent loop error");
            }

            await UpdateSessionStateAsync();
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task<string> ResolveServerUrlAsync(string? configuredUrl, CancellationToken ct)
    {
        // If a specific URL is configured (not localhost), use it directly
        if (!string.IsNullOrWhiteSpace(configuredUrl) && 
            !configuredUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
            !configuredUrl.Contains("127.0.0.1"))
        {
            return configuredUrl;
        }

        // Try auto-discovery
        _logger.LogInformation("No server URL configured, attempting LAN discovery...");
        var discoveredUrl = await ServerDiscovery.DiscoverServerWithRetryAsync(3, _logger);
        
        if (!string.IsNullOrWhiteSpace(discoveredUrl))
        {
            _logger.LogInformation("Using discovered server: {ServerUrl}", discoveredUrl);
            return discoveredUrl;
        }

        // Fall back to default (handle null AND empty string)
        var fallback = string.IsNullOrWhiteSpace(configuredUrl) ? "http://localhost:5081" : configuredUrl;
        _logger.LogWarning("Discovery failed, falling back to: {FallbackUrl}", fallback);
        return fallback;
    }


    private async Task EnsureRegisteredWithRetryAsync(HttpClient http, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromMinutes(1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureRegisteredAsync(http, ct);
                return; // Success - exit retry loop
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Registration failed (server unreachable?), retrying in {Delay}s: {Message}", 
                    (int)delay.TotalSeconds, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration failed unexpectedly, retrying in {Delay}s", (int)delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (TaskCanceledException)
            {
                return; // Service is stopping
            }

            // Exponential backoff up to max
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }
    }


    private async Task EnsureRegisteredAsync(HttpClient http, CancellationToken ct)
    {
        if (_deviceId != Guid.Empty && !string.IsNullOrWhiteSpace(_deviceKey)) return;

        var hostname = Environment.MachineName;
        var os = Environment.OSVersion.VersionString;
        var agentVersion = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        var req = new { Hostname = hostname, OsVersion = os, AgentVersion = agentVersion, Token = (string?)null };
        var resp = await http.PostAsJsonAsync("api/devices/register", req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(cancellationToken: ct);
        if (body == null) throw new InvalidOperationException("Empty registration response");
        _deviceId = body.DeviceId;
        _deviceKey = body.DeviceKey;
        _logger.LogInformation("Registered device {DeviceId}", _deviceId);
    }

    private async Task<bool> SendHeartbeatAsync(HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_deviceKey)) return false;

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/devices/heartbeat");
        req.Headers.Add("X-Device-Key", _deviceKey);
        var payload = new DeviceHeartbeatRequest
        {
            CpuPercent = 0,
            MemPercent = 0,
            ActiveUser = Environment.UserName,
            Ip = "",
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64)
        };
        req.Content = JsonContent.Create(payload);
        var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return false;
        }
        resp.EnsureSuccessStatusCode();
        return true;
    }

    private async Task PollAndExecuteAsync(HttpClient http, CancellationToken ct)
    {
        if (_deviceId == Guid.Empty || string.IsNullOrWhiteSpace(_deviceKey)) return;
        var cmds = await http.GetFromJsonAsync<List<CommandView>>($"api/devices/{_deviceId}/commands?max=5", ct) ?? new();
        foreach (var cmd in cmds)
        {
            string status = "done";
            string? result = null;
            try
            {
                switch (cmd.Type?.ToLowerInvariant())
                {
                    case "restart":
                        await ExecuteRestartAsync();
                        break;
                    case "lock":
                        await SetLockStateAsync(true);
                        break;
                    case "unlock":
                        await SetLockStateAsync(false);
                        break;
                    case "message":
                        await ExecuteMessageAsync(cmd.Payload);
                        break;
                    case "session_set":
                        await HandleSessionSetAsync(cmd.Payload);
                        break;
                    default:
                        status = "ignored";
                        result = "Unknown command";
                        break;
                }
            }
            catch (Exception ex)
            {
                status = "failed";
                result = ex.Message;
            }

            var ack = new AckCommandRequest { Status = status, Result = result };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/commands/{cmd.Id}/ack");
            req.Content = JsonContent.Create(ack);
            var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
    }

    private Task HandleSessionSetAsync(object? payload)
    {
        try
        {
            // payload: { endUtc: "2025-09-05T...Z" }
            var json = payload?.ToString();
            if (!string.IsNullOrWhiteSpace(json))
            {
                var marker = "\"endUtc\":";
                var idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = json.IndexOf('"', idx + marker.Length);
                    var end = json.IndexOf('"', start + 1);
                    if (start > 0 && end > start)
                    {
                        var iso = json.Substring(start + 1, end - start - 1);
                        if (DateTimeOffset.TryParse(iso, out var dt))
                        {
                            _sessionEndUtc = dt;
                            _logger.LogInformation("Session end set to {End}", _sessionEndUtc);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse session_set payload");
        }
        return Task.CompletedTask;
    }

    private Task UpdateSessionStateAsync()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "ClubAgent");
            Directory.CreateDirectory(dir);
            // Agent heartbeat marker so the launcher can fail-open if the service is down
            var hbPath = Path.Combine(dir, "agent_heartbeat.txt");
            File.WriteAllText(hbPath, DateTimeOffset.UtcNow.ToString("O"));
            var path = Path.Combine(dir, "state.json");

            bool isLocked = false;
            long remaining = 0;

            if (_sessionEndUtc.HasValue)
            {
                var now = DateTimeOffset.UtcNow;
                var left = _sessionEndUtc.Value - now;
                if (left <= TimeSpan.Zero)
                {
                    isLocked = true;
                    remaining = 0;
                }
                else
                {
                    remaining = (long)left.TotalSeconds;
                }
            }

            // Merge with existing lock file state if present
            bool fileIsLocked = false;
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path);
                fileIsLocked = content.Contains("\"isLocked\":true", StringComparison.OrdinalIgnoreCase);
            }
            var finalLocked = isLocked || fileIsLocked;

            var json = "{\"isLocked\":" + (finalLocked ? "true" : "false") + ",\"remainingSeconds\":" + remaining + "}";
            File.WriteAllText(path, json);
        }
        catch
        {
            // ignore
        }
        return Task.CompletedTask;
    }

    private Task ExecuteRestartAsync()
    {
        // Schedule a restart in 5 seconds to allow ACK to reach server
        var psi = new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 5")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process.Start(psi);
        return Task.CompletedTask;
    }

    private Task ExecuteMessageAsync(object? payload)
    {
        // For MVP, log to event log; UI popup will come later via helper
        var text = payload?.ToString() ?? "";
        _logger.LogInformation("MESSAGE: {Text}", text);
        return Task.CompletedTask;
    }

    private Task SetLockStateAsync(bool isLocked)
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "ClubAgent");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "state.json");
            var json = "{\"isLocked\":" + (isLocked ? "true" : "false") + "}";
            File.WriteAllText(path, json);
            _logger.LogInformation("Lock state set to {IsLocked}", isLocked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set lock state");
        }
        return Task.CompletedTask;
    }

    private sealed class CommandView
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public object? Payload { get; set; }
    }

    private sealed class AckCommandRequest
    {
        public string Status { get; set; } = string.Empty;
        public string? Result { get; set; }
    }

    private sealed class DeviceRegistrationResponse
    {
        public Guid DeviceId { get; set; }
        public string DeviceKey { get; set; } = string.Empty;
    }

    private sealed class DeviceHeartbeatRequest
    {
        public double CpuPercent { get; set; }
        public double MemPercent { get; set; }
        public string? ActiveUser { get; set; }
        public string Ip { get; set; } = string.Empty;
        public TimeSpan Uptime { get; set; }
    }
}
