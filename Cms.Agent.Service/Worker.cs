using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Cms.Agent.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    private Guid _deviceId;
    private string? _deviceKey;
    private DateTimeOffset? _sessionEndUtc;
    private bool _manualLockRequested;
    private string? _serverUrl;

    // Performance counters for real metrics
    private PerformanceCounter? _cpuCounter;

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitializeCounters();
        LoadRuntimeState();

        var configuredUrl = _config["Server:BaseUrl"];
        var explicitServerUrl = IsExplicitServerUrl(configuredUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            var baseUrl = await ResolveServerUrlAsync(configuredUrl, stoppingToken);
            _serverUrl = NormalizeBaseUrl(baseUrl);
            WriteLauncherConfig();

            _logger.LogInformation("Agent connecting to server URL: {BaseUrl}", _serverUrl);

            var http = _httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(_serverUrl + "/");

            var registered = await EnsureRegisteredWithRetryAsync(http, stoppingToken, retryForever: explicitServerUrl);
            if (!registered)
            {
                await UpdateSessionStateAsync();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            var rediscover = false;
            while (!stoppingToken.IsCancellationRequested && !rediscover)
            {
                try
                {
                    var hbOk = await SendHeartbeatAsync(http, stoppingToken);
                    if (!hbOk)
                    {
                        _logger.LogWarning("Heartbeat unauthorized; re-registering");
                        _deviceId = Guid.Empty;
                        _deviceKey = null;
                        registered = await EnsureRegisteredWithRetryAsync(http, stoppingToken, retryForever: explicitServerUrl);
                        if (!registered)
                        {
                            rediscover = true;
                            break;
                        }
                    }
                    await PollAndExecuteAsync(http, stoppingToken);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Server connection failed");
                    rediscover = !explicitServerUrl;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Agent loop error");
                }

                await UpdateSessionStateAsync();
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private static bool IsExplicitServerUrl(string? configuredUrl)
    {
        return !string.IsNullOrWhiteSpace(configuredUrl) &&
               !configuredUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
               !configuredUrl.Contains("127.0.0.1");
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        return baseUrl.Trim().TrimEnd('/');
    }

    private void WriteLauncherConfig()
    {
        if (string.IsNullOrWhiteSpace(_serverUrl)) return;

        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "ClubAgent");
            Directory.CreateDirectory(dir);
            var configPath = Path.Combine(dir, "config.json");
            var json = JsonSerializer.Serialize(new LauncherConfig(_serverUrl), JsonOptions);
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write launcher config");
        }
    }

    private void LoadRuntimeState()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var path = Path.Combine(programData, "ClubAgent", "state.json");
            if (!File.Exists(path)) return;

            var state = JsonSerializer.Deserialize<AgentState>(File.ReadAllText(path), JsonOptions);
            if (state == null) return;

            _sessionEndUtc = state.SessionEndUtc;
            _manualLockRequested = state.IsLocked &&
                                   string.Equals(state.LockReason, "manual", StringComparison.OrdinalIgnoreCase);
            _serverUrl = state.ServerUrl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load runtime state");
        }
    }

    private void InitializeCounters()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // First call always returns 0, prime it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize performance counters — metrics will be estimated");
        }
    }

    private double GetCpuPercent()
    {
        try
        {
            return _cpuCounter?.NextValue() ?? 0;
        }
        catch { return 0; }
    }

    private double GetMemPercent()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            var totalMem = gcInfo.TotalAvailableMemoryBytes;
            if (totalMem <= 0) return 0;
            var usedMem = totalMem - gcInfo.TotalAvailableMemoryBytes + 
                          (long)(Environment.WorkingSet);
            // Simple approximation using process working set vs total RAM
            return Math.Round((double)Environment.WorkingSet / totalMem * 100, 1);
        }
        catch { return 0; }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? "";
        }
        catch
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip.ToString();
                }
            }
            catch { }
            return "";
        }
    }

    private async Task<string> ResolveServerUrlAsync(string? configuredUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl) && 
            !configuredUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
            !configuredUrl.Contains("127.0.0.1"))
        {
            return configuredUrl;
        }

        _logger.LogInformation("No server URL configured, attempting LAN discovery...");
        var discoveredUrl = await ServerDiscovery.DiscoverServerWithRetryAsync(3, _logger);
        
        if (!string.IsNullOrWhiteSpace(discoveredUrl))
        {
            _logger.LogInformation("Using discovered server: {ServerUrl}", discoveredUrl);
            return discoveredUrl;
        }

        var fallback = string.IsNullOrWhiteSpace(configuredUrl) ? "http://localhost:5081" : configuredUrl;
        _logger.LogWarning("Discovery failed, falling back to: {FallbackUrl}", fallback);
        return fallback;
    }


    private async Task<bool> EnsureRegisteredWithRetryAsync(HttpClient http, CancellationToken ct, bool retryForever)
    {
        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromMinutes(1);
        var attempts = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureRegisteredAsync(http, ct);
                return true;
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

            attempts++;
            if (!retryForever && attempts >= 3)
            {
                _logger.LogWarning("Registration failed after {Attempts} attempts; rediscovery will run again", attempts);
                return false;
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (TaskCanceledException)
            {
                return false;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }

        return false;
    }


    private async Task EnsureRegisteredAsync(HttpClient http, CancellationToken ct)
    {
        if (_deviceId == Guid.Empty || string.IsNullOrWhiteSpace(_deviceKey))
        {
            LoadDeviceCredentials();
        }
        
        if (_deviceId != Guid.Empty && !string.IsNullOrWhiteSpace(_deviceKey)) return;

        var hostname = Environment.MachineName;
        var os = Environment.OSVersion.VersionString;
        var agentVersion = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        var req = new { Hostname = hostname, OsVersion = os, AgentVersion = agentVersion, Token = (string?)null };
        var resp = await http.PostAsJsonAsync("api/devices/register", req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(cancellationToken: ct);
        if (body == null) throw new InvalidOperationException("Empty registration response");
        _deviceId = body.DeviceId;
        _deviceKey = body.DeviceKey;
        
        SaveDeviceCredentials();
        _logger.LogInformation("Registered device {DeviceId}", _deviceId);
    }

    private void LoadDeviceCredentials()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var path = Path.Combine(programData, "ClubAgent", "device.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var idMatch = System.Text.RegularExpressions.Regex.Match(json, "\"deviceId\":\\s*\"([^\"]+)\"");
                var keyMatch = System.Text.RegularExpressions.Regex.Match(json, "\"deviceKey\":\\s*\"([^\"]+)\"");
                if (idMatch.Success && keyMatch.Success && Guid.TryParse(idMatch.Groups[1].Value, out var id))
                {
                    _deviceId = id;
                    _deviceKey = keyMatch.Groups[1].Value;
                    _logger.LogInformation("Loaded device credentials from disk: {DeviceId}", _deviceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load device credentials");
        }
    }

    private void SaveDeviceCredentials()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "ClubAgent");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "device.json");
            var json = $"{{\"deviceId\":\"{_deviceId}\",\"deviceKey\":\"{_deviceKey}\"}}";
            File.WriteAllText(path, json);
            _logger.LogDebug("Saved device credentials to disk");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save device credentials");
        }
    }

    private async Task<bool> SendHeartbeatAsync(HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_deviceKey)) return false;

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/devices/heartbeat");
        req.Headers.Add("X-Device-Key", _deviceKey);
        var payload = new DeviceHeartbeatRequest
        {
            CpuPercent = GetCpuPercent(),
            MemPercent = GetMemPercent(),
            ActiveUser = Environment.UserName,
            Ip = GetLocalIpAddress(),
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

        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"api/devices/{_deviceId}/commands?max=5");
        pollReq.Headers.Add("X-Device-Key", _deviceKey);
        var pollResp = await http.SendAsync(pollReq, ct);
        if (pollResp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Command poll unauthorized; re-registering");
            _deviceId = Guid.Empty;
            _deviceKey = null;
            return;
        }
        pollResp.EnsureSuccessStatusCode();

        var cmds = await pollResp.Content.ReadFromJsonAsync<List<CommandView>>(cancellationToken: ct) ?? new();
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
                    case "logoff":
                        await ExecuteLogoffAsync();
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
            req.Headers.Add("X-Device-Key", _deviceKey);
            req.Content = JsonContent.Create(ack);
            try
            {
                var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ACK command {CommandId}", cmd.Id);
            }
        }
    }

    private Task HandleSessionSetAsync(object? payload)
    {
        try
        {
            if (TryReadEndUtc(payload, out var endUtc))
            {
                _sessionEndUtc = endUtc;
                _manualLockRequested = false;
                _logger.LogInformation("Session end set to {End}", _sessionEndUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse session_set payload");
        }
        return Task.CompletedTask;
    }

    private static bool TryReadEndUtc(object? payload, out DateTimeOffset endUtc)
    {
        endUtc = default;

        if (payload is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("endUtc", out var endProperty) &&
                endProperty.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(endProperty.GetString(), out endUtc))
            {
                return true;
            }

            return false;
        }

        var json = payload?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("endUtc", out var endProperty) &&
                endProperty.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(endProperty.GetString(), out endUtc))
            {
                return true;
            }
        }
        catch
        {
            // Legacy fallback for unexpected payload formats.
            var marker = "\"endUtc\":";
            var idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = json.IndexOf('"', idx + marker.Length);
                var end = json.IndexOf('"', start + 1);
                if (start > 0 && end > start)
                {
                    var iso = json.Substring(start + 1, end - start - 1);
                    return DateTimeOffset.TryParse(iso, out endUtc);
                }
            }
        }

        return false;
    }

    private Task UpdateSessionStateAsync()
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "ClubAgent");
            Directory.CreateDirectory(dir);

            // Agent heartbeat marker
            var hbPath = Path.Combine(dir, "agent_heartbeat.txt");
            File.WriteAllText(hbPath, DateTimeOffset.UtcNow.ToString("O"));
            var path = Path.Combine(dir, "state.json");

            bool sessionExpired = false;
            long remaining = 0;

            if (_sessionEndUtc.HasValue)
            {
                var now = DateTimeOffset.UtcNow;
                var left = _sessionEndUtc.Value - now;
                if (left <= TimeSpan.Zero)
                {
                    sessionExpired = true;
                    remaining = 0;
                }
                else
                {
                    remaining = (long)Math.Ceiling(left.TotalSeconds);
                }
            }

            var finalLocked = _manualLockRequested || sessionExpired;
            var lockReason = _manualLockRequested ? "manual" : sessionExpired ? "session_expired" : "none";

            var json = JsonSerializer.Serialize(new AgentState(
                finalLocked,
                remaining,
                _sessionEndUtc,
                lockReason,
                _serverUrl
            ), JsonOptions);
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
        var psi = new ProcessStartInfo("shutdown", "/r /t 5")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
        _logger.LogInformation("Restart scheduled in 5 seconds");
        return Task.CompletedTask;
    }

    private Task ExecuteLogoffAsync()
    {
        var psi = new ProcessStartInfo("shutdown", "/l")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
        _logger.LogInformation("Logoff initiated");
        return Task.CompletedTask;
    }

    private Task ExecuteMessageAsync(object? payload)
    {
        var text = payload?.ToString() ?? "";
        _logger.LogInformation("MESSAGE: {Text}", text);
        return Task.CompletedTask;
    }

    private Task SetLockStateAsync(bool isLocked)
    {
        try
        {
            _manualLockRequested = isLocked;

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var dir = Path.Combine(programData, "ClubAgent");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "state.json");

            long remaining = 0;
            var sessionExpired = false;
            if (_sessionEndUtc.HasValue)
            {
                var left = _sessionEndUtc.Value - DateTimeOffset.UtcNow;
                if (left > TimeSpan.Zero)
                {
                    remaining = (long)Math.Ceiling(left.TotalSeconds);
                }
                else
                {
                    sessionExpired = true;
                }
            }

            var finalLocked = _manualLockRequested || sessionExpired;
            var lockReason = _manualLockRequested ? "manual" : sessionExpired ? "session_expired" : "none";
            var json = JsonSerializer.Serialize(new AgentState(
                finalLocked,
                remaining,
                _sessionEndUtc,
                lockReason,
                _serverUrl
            ), JsonOptions);
            File.WriteAllText(path, json);
            _logger.LogInformation("Manual lock state set to {IsLocked}", isLocked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set lock state");
        }
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record AgentState(
        bool IsLocked,
        long RemainingSeconds,
        DateTimeOffset? SessionEndUtc,
        string LockReason,
        string? ServerUrl);

    private sealed record LauncherConfig(string ServerUrl);

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
