using System.Net.Http.Json;

namespace Cms.Agent.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    private Guid _deviceId;
    private string? _deviceKey;

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseUrl = _config["Server:BaseUrl"] ?? "http://localhost:5081";
        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(baseUrl);

        await EnsureRegisteredAsync(http, stoppingToken);

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
                    await EnsureRegisteredAsync(http, stoppingToken);
                }
                await PollAndExecuteAsync(http, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
