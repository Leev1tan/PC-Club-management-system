using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Cms.Client;
using Cms.Shared;

namespace Cms.Agent.Service;

/// <summary>
/// Agent on a club PC (Windows service ClubAgent).
///  - Server loop: registers, sends a heartbeat every 5 s, polls commands every second.
///  - Local loop: every second writes the lock state for Cms.Launcher (state.json) and its own
///    heartbeat file, also while the server is down, so the launcher can tell a silent agent.
/// Files in %ProgramData%\ClubAgent:
///    state.json, agent_heartbeat.txt, config.json, staff-pin.json  - readable by the launcher
///    private\device.json, private\enrollment-token.txt            - SYSTEM, Administrators, agent only
///    inbox\event-*.json - written by the launcher (emergency PIN unlock), sent to the server, then deleted
/// </summary>
public partial class Worker : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<Worker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    private static readonly string AgentDir = AgentFiles.Directory;
    private static readonly string PrivateDir = Path.Combine(AgentDir, AgentFiles.PrivateDirectoryName);
    private static readonly string InboxDir = Path.Combine(AgentDir, AgentFiles.InboxDirectoryName);
    private const int MaxInboxFiles = 500;
    private const int EventsPerHeartbeat = 20;

    // Lock state, shared by the server loop (commands) and the local loop (state.json).
    // A club PC is locked unless a session runs on it or staff unlocked it (Cms.Client.AgentLock).
    private readonly object _gate = new();
    private AgentLock _lock = AgentLock.Initial;
    private ClientSessionInfo? _session;
    /// <summary>"Log off" of the staff, for the lock screen (a service in session 0 cannot log off the player).</summary>
    private LogoffRequest? _logoff;
    /// <summary>The reservation that holds this PC now (heartbeat), for the lock screen.</summary>
    private ClientReservationInfo? _reservation;
    private bool? _lastLocked;
    private DateTimeOffset _lockChangedUtc = DateTimeOffset.UtcNow;
    /// <summary>The status reports of the lock screen (pipe "status" every 5 s): a silent lock screen is logged.</summary>
    private readonly LockScreenWatch _screen = new(DateTimeOffset.UtcNow);
    private string? _serverUrl;

    // Registration
    private Guid _deviceId;
    private string? _deviceKey;
    private bool _credentialsConfirmed;
    private string _enrollmentStatus = "unknown";
    private bool _pendingLogged;
    private DateTimeOffset _nextHeartbeatUtc;

    // Game catalog of this PC (games.json for the launcher), fetched every 30 s with If-None-Match
    private static readonly TimeSpan CatalogInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _nextCatalogUtc;
    private string? _catalogETag;

    // Performance counters for real metrics
    private PerformanceCounter? _cpuCounter;

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    /// <summary>
    /// The agent never stops on a server, network or timeout error (Windows test 2026-11-27, defect 1): each loop runs
    /// in AgentLoops.RunForeverAsync and starts again after an error; only the stop of the service ends them. Program.cs
    /// also sets BackgroundServiceExceptionBehavior.Ignore, and the installer sets restart actions for the service.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClubAgent {Version} started (process {Pid}; log folder {LogDir})",
            typeof(Worker).Assembly.GetName().Version?.ToString() ?? "?", Environment.ProcessId, AgentLog.Directory);
        // Each start step logs its own error; a failed step never stops the agent (the PC stays locked)
        Step("create the agent folder", () => Directory.CreateDirectory(AgentDir));
        Step("create the private folder", EnsurePrivateDirectory);
        Step("create the inbox", EnsureInboxDirectory);
        Step("move old credentials", MigrateLegacyCredentials);
        Step("write teardown.json", WriteTeardownSettings);
        Step("write account-pool.json", WriteAccountPoolSettings);
        Step("start the CPU counter", InitializeCounters);
        Step("load state.json", LoadRuntimeState);

        await Task.WhenAll(
            Forever("local state loop", RunLocalStateLoopAsync, stoppingToken),
            Forever("launcher pipe", RunLauncherPipeAsync, stoppingToken),
            Forever("server loop", RunServerLoopAsync, stoppingToken));
    }

    private void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _logger.LogError(ex, "Agent start: could not {What}; the agent goes on", what); }
    }

    private Task Forever(string name, Func<CancellationToken, Task> loop, CancellationToken stoppingToken) =>
        AgentLoops.RunForeverAsync(loop, ex =>
        {
            if (ex == null) _logger.LogWarning("The {Loop} ended; it starts again in 5 s", name);
            else _logger.LogError(ex, "The {Loop} failed; it starts again in 5 s", name);
        }, TimeSpan.FromSeconds(5), stoppingToken);

    // ──── LOCAL STATE FOR THE LAUNCHER ────

    private async Task RunLocalStateLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                WriteLocalState();
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // service stops
        }
    }

    /// <summary>
    /// Writes state.json and agent_heartbeat.txt (atomic replace). The session end time is enforced
    /// here, so a timed session also locks while the server is unreachable.
    /// </summary>
    private void WriteLocalState()
    {
        try
        {
            AgentState state;
            LockScreenEvent? screenEvent;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var decision = _lock.Evaluate(now);
                screenEvent = _screen.Check(now, decision.Locked);
                if (_lastLocked != decision.Locked)
                {
                    _lastLocked = decision.Locked;
                    _lockChangedUtc = now;
                }
                if (_logoff != null && now - _logoff.RequestedUtc > LogoffTrigger.MaxAge) _logoff = null;
                state = new AgentState(decision.Locked, decision.RemainingSeconds, _lock.SessionEndUtc, decision.Reason, _serverUrl,
                    _lockChangedUtc, _enrollmentStatus)
                {
                    Unlock = _lock.Unlock,
                    LockGeneration = _lock.Generation,
                    AgentPid = Environment.ProcessId,
                    Session = _session,
                    Logoff = _logoff,
                    Reservation = _reservation
                };
            }

            WriteAtomic(Path.Combine(AgentDir, AgentFiles.StateFile), JsonSerializer.Serialize(state, JsonOptions));
            WriteAtomic(Path.Combine(AgentDir, AgentFiles.HeartbeatFile), DateTimeOffset.UtcNow.ToString("O"));
            LogLockScreenEvent(screenEvent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write the local state");
        }
    }

    private void LogLockScreenEvent(LockScreenEvent? screenEvent)
    {
        if (screenEvent == LockScreenEvent.SilentWhileLocked)
        {
            DateTimeOffset? last;
            lock (_gate) last = _screen.LastReportUtc;
            _logger.LogWarning("The lock screen does not answer ({Since}) while this PC must be locked: the screen may be empty " +
                               "or open. The watchdog of the kiosk session starts it again; if it does not come back, restart the PC",
                last is { } l ? $"last status {(int)(DateTimeOffset.UtcNow - l).TotalSeconds} s ago" : "no status since the agent start");
        }
        else if (screenEvent == LockScreenEvent.Back)
        {
            _logger.LogInformation("The lock screen answers again");
        }
    }

    /// <summary>Write to a temporary file, then replace: the launcher never reads a half-written file.</summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
            // The launcher holds the file open at this moment; write in place this time
            File.WriteAllText(path, content);
            try { File.Delete(tmp); } catch { /* next tick */ }
        }
    }

    /// <summary>
    /// The state of the last run. Without state.json, or when it cannot be read, the PC stays locked. Only an unlock
    /// for a session survives the restart; a staff unlock without a session does not (Cms.Client.AgentLock.Restore).
    /// </summary>
    private void LoadRuntimeState()
    {
        try
        {
            var path = Path.Combine(AgentDir, AgentFiles.StateFile);
            if (!File.Exists(path))
            {
                _logger.LogInformation("No saved state: the PC is locked until a session starts or staff unlock it");
                return;
            }

            var state = JsonSerializer.Deserialize<AgentState>(File.ReadAllText(path), JsonOptions);
            if (state == null) return;

            lock (_gate)
            {
                _lock = AgentLock.Restore(new SavedAgentLock(state.IsLocked, state.LockReason, state.Unlock, state.SessionEndUtc, state.Session != null,
                    state.LockGeneration));
                _serverUrl = state.ServerUrl;
            }
            _logger.LogInformation("Saved state restored: {State}", _lock.Unlock == AgentLock.UnlockSession
                ? "unlocked for the session (until its end)" : "locked until a session starts or staff unlock the PC");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load runtime state; the PC stays locked");
        }
    }

    // ──── SERVER LOOP ────

    private async Task RunServerLoopAsync(CancellationToken stoppingToken)
    {
        var configuredUrl = _config["Server:BaseUrl"];
        var explicitServerUrl = IsExplicitServerUrl(configuredUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(configuredUrl, explicitServerUrl, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // For example a server URL that is not a valid URI: logged, tried again (the PC stays locked)
                _logger.LogError(ex, "Server connection failed; trying again in 10 s");
                await AgentLoops.DelayAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>Finds the server, registers, then sends heartbeats and polls commands until the server must be found again.</summary>
    private async Task ConnectAndServeAsync(string? configuredUrl, bool explicitServerUrl, CancellationToken stoppingToken)
    {
        {
            var baseUrl = await ResolveServerUrlAsync(configuredUrl, stoppingToken);
            lock (_gate) _serverUrl = NormalizeBaseUrl(baseUrl);
            WriteLauncherConfig();

            _logger.LogInformation("Agent connecting to server URL: {BaseUrl}", _serverUrl);

            var http = _httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(_serverUrl + "/");
            http.Timeout = TimeSpan.FromSeconds(15);

            // The enrollment token goes only to an explicitly configured server, never to a server
            // found by (spoofable) UDP discovery. A discovered server puts the PC in the approval queue.
            var registered = await EnsureRegisteredWithRetryAsync(http, stoppingToken, retryForever: explicitServerUrl, allowToken: explicitServerUrl);
            if (!registered)
            {
                await AgentLoops.DelayAsync(TimeSpan.FromSeconds(2), stoppingToken);
                return;
            }

            var rediscover = false;
            while (!stoppingToken.IsCancellationRequested && !rediscover)
            {
                try
                {
                    if (DateTimeOffset.UtcNow >= _nextHeartbeatUtc)
                    {
                        _nextHeartbeatUtc = DateTimeOffset.UtcNow + HeartbeatInterval;
                        var hb = await SendHeartbeatAsync(http, stoppingToken);
                        if (hb == HeartbeatResult.Ok) DeleteEnrollmentTokenFile();
                        if (hb == HeartbeatResult.Ok && _enrollmentStatus == "approved")
                        {
                            await SendLauncherEventsAsync(http, stoppingToken);
                            if (DateTimeOffset.UtcNow >= _nextCatalogUtc)
                            {
                                _nextCatalogUtc = DateTimeOffset.UtcNow + CatalogInterval;
                                await SyncCatalogAsync(http, stoppingToken);
                            }
                        }
                        if (hb == HeartbeatResult.Unauthorized)
                        {
                            _logger.LogWarning("The server does not know this device key any more; registering again");
                            ClearCredentials();
                            if (!await EnsureRegisteredWithRetryAsync(http, stoppingToken, retryForever: explicitServerUrl, allowToken: explicitServerUrl))
                            {
                                rediscover = true;
                                break;
                            }
                        }
                    }
                    if (_enrollmentStatus == "approved")
                        await PollAndExecuteAsync(http, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Every error is logged and tried again; only the stop of the service ends this loop
                    switch (AgentLoops.Classify(ex))
                    {
                        case AgentLoops.ErrorKind.Network:
                            _logger.LogWarning("Server connection failed: {Message}", ex.Message);
                            rediscover = !explicitServerUrl;
                            break;
                        case AgentLoops.ErrorKind.Timeout:
                            _logger.LogWarning("Server request timed out");
                            break;
                        default:
                            _logger.LogError(ex, "Agent loop error");
                            break;
                    }
                }

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
            var json = JsonSerializer.Serialize(new LauncherConfig(_serverUrl), JsonOptions);
            WriteAtomic(Path.Combine(AgentDir, AgentFiles.ConfigFile), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write launcher config");
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

    private static double GetMemPercent()
    {
        try
        {
            var totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (totalMem <= 0) return 0;
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
        if (IsExplicitServerUrl(configuredUrl))
            return configuredUrl!;

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

    // ──── REGISTRATION ────

    /// <summary>
    /// Registers until it works. A server that is off, a network that is down or a request that times out is logged and
    /// tried again (AgentLoops.RetryAsync, tested with a server that never answers); it never ends the agent.
    /// </summary>
    private Task<bool> EnsureRegisteredWithRetryAsync(HttpClient http, CancellationToken ct, bool retryForever, bool allowToken) =>
        AgentLoops.RetryAsync(token => EnsureRegisteredAsync(http, allowToken, token), (ex, delay) =>
        {
            var next = delay > TimeSpan.Zero ? $"retrying in {(int)delay.TotalSeconds}s" : "the server is looked for again";
            if (ex is RegistrationRefusedException)
                _logger.LogError("Registration refused by the server: {Message}. Check the enrollment token " +
                                 "(installer option -EnrollmentToken) or approve the PC in the admin UI; {Next}", ex.Message, next);
            else if (AgentLoops.Classify(ex) == AgentLoops.ErrorKind.Timeout)
                _logger.LogWarning("Registration: the server did not answer in time; {Next}", next);
            else if (AgentLoops.Classify(ex) == AgentLoops.ErrorKind.Network)
                _logger.LogWarning("Registration failed (server unreachable?): {Message}; {Next}", ex.Message, next);
            else
                _logger.LogError(ex, "Registration failed unexpectedly; {Next}", next);
        }, retryForever, maxAttempts: 3, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), ct);

    /// <summary>
    /// With stored credentials: re-registers with the current key (updates OS and agent version).
    /// Without: registers with the enrollment token (approved at once) or without it (pending
    /// until staff approve the PC in the admin UI). The server never hands out the key of a known PC.
    /// </summary>
    private async Task EnsureRegisteredAsync(HttpClient http, bool allowToken, CancellationToken ct)
    {
        if (_deviceId == Guid.Empty || string.IsNullOrWhiteSpace(_deviceKey))
            LoadDeviceCredentials();
        var haveCredentials = _deviceId != Guid.Empty && !string.IsNullOrWhiteSpace(_deviceKey);
        if (haveCredentials && _credentialsConfirmed) return;

        var hostname = Environment.MachineName;
        var os = Environment.OSVersion.VersionString;
        var agentVersion = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var (token, _) = allowToken && !haveCredentials ? ReadEnrollmentToken() : (null, false);

        object req = haveCredentials
            ? new { Hostname = hostname, OsVersion = os, AgentVersion = agentVersion, DeviceId = _deviceId, DeviceKey = _deviceKey }
            : new { Hostname = hostname, OsVersion = os, AgentVersion = agentVersion, Token = token };
        using var resp = await http.PostAsJsonAsync("api/devices/register", req, ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var error = await ReadErrorAsync(resp, ct);
            if (haveCredentials)
            {
                // Key revoked or unknown on this server: start over as a new registration
                ClearCredentials();
                throw new RegistrationRefusedException("stored device key rejected (" + error + ")");
            }
            throw new RegistrationRefusedException(error);
        }
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.BadRequest)
            throw new RegistrationRefusedException($"{(int)resp.StatusCode}: {await ReadErrorAsync(resp, ct)}");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(JsonOptions, ct)
                   ?? throw new InvalidOperationException("Empty registration response");
        _deviceId = body.DeviceId;
        _deviceKey = body.DeviceKey;
        _credentialsConfirmed = true;
        SetEnrollmentStatus(body.Status);
        SaveDeviceCredentials();

        _logger.LogInformation("Registered device {DeviceId} (status {Status})", _deviceId, body.Status);
        DeleteEnrollmentTokenFile();
    }

    private bool _tokenFileChecked;

    /// <summary>
    /// The server accepted this PC (a registration or a heartbeat): the enrollment token is not needed on the PC any more.
    /// Windows test 2026-11-27, defect 7: the file stayed on every updated PC, because it was deleted only after a first
    /// registration with it. Checked once per agent start; the installer no longer writes it on a registered PC.
    /// </summary>
    private void DeleteEnrollmentTokenFile()
    {
        if (_tokenFileChecked) return;
        _tokenFileChecked = true;
        var path = Path.Combine(PrivateDir, AgentFiles.EnrollmentTokenFile);
        try
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            _logger.LogInformation("Deleted the enrollment token file: this PC is registered, the token is not needed any more");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete the enrollment token file {Path}: {Error}", path, ex.Message);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? text : text;
        }
        catch
        {
            return resp.ReasonPhrase ?? resp.StatusCode.ToString();
        }
    }

    /// <summary>private\enrollment-token.txt (written by the installer), or Server:EnrollmentToken.</summary>
    private (string? Token, bool FromFile) ReadEnrollmentToken()
    {
        try
        {
            var path = Path.Combine(PrivateDir, AgentFiles.EnrollmentTokenFile);
            if (File.Exists(path))
            {
                var token = File.ReadAllText(path).Trim();
                if (token.Length > 0) return (token, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the enrollment token file");
        }
        var configured = _config["Server:EnrollmentToken"];
        return string.IsNullOrWhiteSpace(configured) ? (null, false) : (configured.Trim(), false);
    }

    private void SetEnrollmentStatus(string? status)
    {
        lock (_gate) _enrollmentStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status;
        if (_enrollmentStatus == "pending")
        {
            if (!_pendingLogged)
                _logger.LogWarning("This PC waits for approval. Approve it in the admin UI (Devices), " +
                                   "or install the agent with the enrollment token.");
            _pendingLogged = true;
        }
        else
        {
            _pendingLogged = false;
        }
    }

    /// <summary>
    /// The private folder holds the device key. Only SYSTEM, Administrators and the agent service
    /// account get access (the installer also sets this ACL), so a player cannot read the key.
    /// </summary>
    private void EnsurePrivateDirectory()
    {
        if (Directory.Exists(PrivateDir)) return;
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            void Allow(SecurityIdentifier sid) => security.AddAccessRule(new FileSystemAccessRule(sid,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            var self = WindowsIdentity.GetCurrent().User;
            if (self != null) Allow(self);
            new DirectoryInfo(PrivateDir).Create(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create {Dir} with a restricted ACL", PrivateDir);
            Directory.CreateDirectory(PrivateDir);
        }
    }

    /// <summary>
    /// The inbox of the launcher: players (Users) may only create files in it; they cannot list,
    /// read, change or delete them (OWNER RIGHTS gets only read-permissions on the new files), so a
    /// player cannot remove the note of an emergency unlock. The installer sets the same ACL.
    /// </summary>
    private void EnsureInboxDirectory()
    {
        if (Directory.Exists(InboxDir)) return;
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            var self = WindowsIdentity.GetCurrent().User;
            if (self != null)
                security.AddAccessRule(new FileSystemAccessRule(self, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.CreateFiles | FileSystemRights.Synchronize, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), // OWNER RIGHTS
                FileSystemRights.ReadPermissions, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            new DirectoryInfo(InboxDir).Create(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create {Dir} with the inbox ACL; emergency unlocks are not reported", InboxDir);
        }
    }

    /// <summary>
    /// Sends the launcher's notes (emergency PIN unlock) to the server's audit log. A file is deleted
    /// when the server accepted it (or refused it as invalid); otherwise it is sent again later, also
    /// after the server or the agent was down.
    /// </summary>
    private async Task SendLauncherEventsAsync(HttpClient http, CancellationToken ct)
    {
        List<FileInfo> files;
        try
        {
            if (!Directory.Exists(InboxDir)) return;
            files = new DirectoryInfo(InboxDir).GetFiles("event-*.json").OrderBy(f => f.CreationTimeUtc).ToList();
            foreach (var old in files.Take(Math.Max(0, files.Count - MaxInboxFiles))) TryDelete(old);
            files = files.Skip(Math.Max(0, files.Count - MaxInboxFiles)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot read the launcher inbox");
            return;
        }

        foreach (var file in files.Take(EventsPerHeartbeat))
        {
            LauncherEvent? e = null;
            try
            {
                if (file.Length <= LauncherEvent.MaxFileBytes)
                    e = JsonSerializer.Deserialize<LauncherEvent>(File.ReadAllText(file.FullName), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // half-written or garbage: dropped below (a JsonException), or tried again next time
                if (ex is not JsonException) continue;
            }
            if (e == null || e.Id == Guid.Empty)
            {
                TryDelete(file);
                continue;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/events");
            req.Headers.Add("X-Device-Key", _deviceKey);
            req.Content = JsonContent.Create(e);
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.BadRequest)
            {
                if (resp.IsSuccessStatusCode) _logger.LogInformation("Reported the emergency unlock of {Time:O} to the server", e.OccurredUtc);
                TryDelete(file);
            }
            else
            {
                break; // 429 (too many) or server trouble: keep the files for the next heartbeat
            }
        }
    }

    /// <summary>
    /// Keeps games.json equal to the catalog of this PC on the server (active entries of its zone). The
    /// file stays when the server is down, so the game grid works offline with the last catalog.
    /// </summary>
    private async Task SyncCatalogAsync(HttpClient http, CancellationToken ct)
    {
        var path = Path.Combine(AgentDir, AgentFiles.CatalogFile);
        try
        {
            _catalogETag ??= File.Exists(path) ? CatalogFile.Parse(File.ReadAllText(path))?.ETag : null;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"api/devices/{_deviceId}/games");
            req.Headers.Add("X-Device-Key", _deviceKey);
            if (_catalogETag != null) req.Headers.TryAddWithoutValidation("If-None-Match", _catalogETag);
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotModified || !resp.IsSuccessStatusCode) return;
            var games = CatalogFile.ParseList(await resp.Content.ReadAsStringAsync(ct));
            if (games == null) return;
            var etag = resp.Headers.ETag?.ToString();
            WriteAtomic(path, new CatalogFile { ETag = etag, ReceivedUtc = DateTimeOffset.UtcNow, Games = games }.ToJson());
            _catalogETag = etag;
            _logger.LogInformation("Game catalog updated: {Count} entries", games.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to update {Path}", path);
        }
    }

    private void TryDelete(FileInfo file)
    {
        try { file.Delete(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {File}", file.FullName); }
    }

    /// <summary>Earlier versions kept device.json (with the key) in the launcher-readable folder.</summary>
    private void MigrateLegacyCredentials()
    {
        try
        {
            var legacy = Path.Combine(AgentDir, AgentFiles.DeviceFile);
            var current = Path.Combine(PrivateDir, AgentFiles.DeviceFile);
            if (!File.Exists(legacy)) return;
            if (!File.Exists(current)) File.Copy(legacy, current);
            File.Delete(legacy);
            _logger.LogInformation("Moved device credentials to {Path}", current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to move the device credentials to the private folder");
        }
    }

    private void LoadDeviceCredentials()
    {
        try
        {
            var path = Path.Combine(PrivateDir, AgentFiles.DeviceFile);
            if (!File.Exists(path)) return;
            var creds = JsonSerializer.Deserialize<DeviceCredentials>(File.ReadAllText(path), JsonOptions);
            if (creds != null && creds.DeviceId != Guid.Empty && !string.IsNullOrWhiteSpace(creds.DeviceKey))
            {
                _deviceId = creds.DeviceId;
                _deviceKey = creds.DeviceKey;
                _credentialsConfirmed = false;
                _logger.LogInformation("Loaded device credentials from disk: {DeviceId}", _deviceId);
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
            var json = JsonSerializer.Serialize(new DeviceCredentials { DeviceId = _deviceId, DeviceKey = _deviceKey ?? "" }, JsonOptions);
            WriteAtomic(Path.Combine(PrivateDir, AgentFiles.DeviceFile), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save device credentials");
        }
    }

    private void ClearCredentials()
    {
        _deviceId = Guid.Empty;
        _deviceKey = null;
        _credentialsConfirmed = false;
        SetEnrollmentStatus("unknown");
        try { File.Delete(Path.Combine(PrivateDir, AgentFiles.DeviceFile)); } catch { /* ignore */ }
    }

    // ──── HEARTBEAT ────

    private enum HeartbeatResult { Ok, Unauthorized }

    private async Task<HeartbeatResult> SendHeartbeatAsync(HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_deviceKey)) return HeartbeatResult.Unauthorized;

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/devices/heartbeat");
        req.Headers.Add("X-Device-Key", _deviceKey);
        var ip = GetLocalIpAddress();
        var payload = new DeviceHeartbeatRequest
        {
            CpuPercent = GetCpuPercent(),
            MemPercent = GetMemPercent(),
            ActiveUser = Environment.UserName,
            Ip = ip,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            // For Wake-on-LAN: the card that reaches the server
            Mac = NetworkIdentity.PickMac(NetworkIdentity.Current(), ip)
        };
        req.Content = JsonContent.Create(payload);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return HeartbeatResult.Unauthorized;
        resp.EnsureSuccessStatusCode();

        try
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var status))
                    SetEnrollmentStatus(status.GetString());
                if (_enrollmentStatus == "approved" && root.TryGetProperty("staffPinHash", out var pin))
                    UpdateStaffPin(pin.ValueKind == JsonValueKind.String ? pin.GetString() : null);
                if (_enrollmentStatus == "approved" && root.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.Object)
                {
                    // The logo first: when the lock screen reads the new settings, the logo file is already there
                    if (ClientSettings.Parse(client.GetRawText()) is { } received) await SyncClubLogoAsync(http, received.LogoSha256, ct);
                    UpdateClientSettings(client.GetRawText());
                }
                if (_enrollmentStatus == "approved" && root.TryGetProperty("session", out var session))
                {
                    var info = session.ValueKind == JsonValueKind.Object ? session.Deserialize<ClientSessionInfo>(JsonOptions) : null;
                    bool relocked;
                    lock (_gate)
                    {
                        _session = info;
                        // No active session on the server: a PC unlocked for a session locks (a staff unlock stays)
                        var before = _lock;
                        _lock = _lock.Heartbeat(sessionActive: session.ValueKind == JsonValueKind.Object);
                        relocked = !ReferenceEquals(before, _lock);
                    }
                    if (relocked)
                        _logger.LogWarning("The server has no active session for this PC: locked");
                }
                if (_enrollmentStatus == "approved" && root.TryGetProperty("reservation", out var reservation))
                {
                    var hold = reservation.ValueKind == JsonValueKind.Object ? reservation.Deserialize<ClientReservationInfo>(JsonOptions) : null;
                    lock (_gate) _reservation = hold;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Unexpected heartbeat response");
        }
        return HeartbeatResult.Ok;
    }

    /// <summary>
    /// Keeps staff-pin.json equal to the PIN hash on the server. The launcher verifies the staff PIN
    /// against this file, so the emergency unlock works offline. No PIN on the server = no file.
    /// </summary>
    private void UpdateStaffPin(string? hash)
    {
        var path = Path.Combine(AgentDir, AgentFiles.StaffPinFile);
        try
        {
            if (hash == null || !StaffPinHash.IsWellFormed(hash))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogInformation("Staff PIN removed on the server; deleted {Path}", path);
                }
                return;
            }

            if (File.Exists(path))
            {
                var current = JsonSerializer.Deserialize<StaffPinFile>(File.ReadAllText(path), JsonOptions);
                if (current?.Hash == hash) return;
            }
            WriteAtomic(path, JsonSerializer.Serialize(new StaffPinFile { Hash = hash, UpdatedUtc = DateTimeOffset.UtcNow }, JsonOptions));
            _logger.LogInformation("Staff PIN updated from the server");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update {Path}", path);
        }
    }

    /// <summary>
    /// Keeps client-settings.json equal to the club settings of the lock screen (language, currency, theme, accent and
    /// the hash of the logo). The file stays when the server is down, so the lock screen keeps the last known club
    /// settings; without the file (the server was never reached) it shows Ukrainian and the Cyber theme.
    /// </summary>
    private void UpdateClientSettings(string json)
    {
        var path = Path.Combine(AgentDir, AgentFiles.ClientSettingsFile);
        try
        {
            var received = ClientSettings.Parse(json);
            if (received == null) return;
            var currentText = File.Exists(path) ? File.ReadAllText(path) : null;
            var current = ClientSettings.Parse(currentText);
            var settings = new ClientSettings
            {
                Language = received.Language, Currency = received.Currency, Theme = received.Theme, Accent = received.Accent,
                LogoSha256 = received.LogoSha256, ReceivedUtc = DateTimeOffset.UtcNow
            };
            // The same values: write only when the file lacks a field (a file of an older agent, for example without "theme")
            if (received.SameAs(current) && !ClientSettings.LacksFields(currentText, settings)) return;
            WriteAtomic(path, settings.ToJson());
            _logger.LogInformation("Lock screen settings from the server: language {Language}, currency {Currency}, theme {Theme}, accent {Accent}, logo {Logo}",
                settings.Language, settings.Currency, settings.Theme, settings.Accent ?? "-", settings.LogoSha256?[..12] ?? "-");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update {Path}", path);
        }
    }

    private string? _logoSynced = "(not checked)";
    private DateTimeOffset _logoRetryUtc;
    private DateTimeOffset _logoCheckUtc;

    /// <summary>
    /// club-logo.img follows the logo hash of the heartbeat (Cms.Client.ClubLogoSync: HTTP 200 only, at most 512 KB, the
    /// SHA-256 must match, temporary file and move). A failed download keeps the old file and is tried again after 60 s.
    /// The file is checked again every 60 s, so a deleted or changed file is repaired.
    /// </summary>
    private async Task SyncClubLogoAsync(HttpClient http, string? sha256, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if ((sha256 == _logoSynced && now < _logoCheckUtc) || now < _logoRetryUtc) return;
        try
        {
            var result = await ClubLogoSync.SyncAsync(http, AgentDir, sha256, ct);
            if (result == ClubLogoSync.Result.Failed)
            {
                _logoRetryUtc = DateTimeOffset.UtcNow.AddSeconds(60);
                _logger.LogWarning("Club logo {Hash}: the download failed or the bytes did not match; next try in 60 s", sha256?[..12]);
                return;
            }
            _logoSynced = sha256;
            _logoCheckUtc = DateTimeOffset.UtcNow.AddSeconds(60);
            if (result != ClubLogoSync.Result.Unchanged)
                _logger.LogInformation("Club logo {Result}: {Hash}", result, sha256?[..12] ?? "-");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Also a timeout of the download: the heartbeat goes on with the old logo
            _logoRetryUtc = DateTimeOffset.UtcNow.AddSeconds(60);
            _logger.LogWarning("Club logo {Hash}: failed ({Error}); next try in 60 s", sha256?[..12], ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// teardown.json for the lock screen, from the section "Teardown" of appsettings.json on this PC (only
    /// administrators can change it; the server never sends it). Without the section: the defaults
    /// (launchers, chat and browsers are closed; cleanup off).
    /// </summary>
    private void WriteTeardownSettings()
    {
        var path = Path.Combine(AgentDir, AgentFiles.TeardownFile);
        try
        {
            var section = _config.GetSection("Teardown");
            var settings = new TeardownSettings
            {
                KillProcessNames = section.GetSection("KillProcessNames").Get<string[]>() ?? TeardownSettings.DefaultKillProcessNames.ToArray(),
                Cleanup = new CleanupSettings
                {
                    Enabled = section.GetValue("Cleanup:Enabled", false),
                    AllowedRoots = section.GetSection("Cleanup:AllowedRoots").Get<string[]>() ?? TeardownSettings.DefaultAllowedRoots.ToArray(),
                    Paths = section.GetSection("Cleanup:Paths").Get<string[]>() ?? Array.Empty<string>()
                }
            };
            WriteAtomic(path, settings.ToJson());
            _logger.LogInformation("Session teardown: {Count} process names; cleanup {Cleanup} ({Paths} paths)",
                settings.KillProcessNames.Count, settings.Cleanup.Enabled ? "on" : "off", settings.Cleanup.Paths.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write {Path}; the lock screen uses the defaults", path);
        }
    }

    /// <summary>
    /// account-pool.json for the lock screen, from the section "AccountPool:Platforms" of appsettings.json on this PC (only
    /// administrators can change it; the server never sends it): how to log in to the client of each platform with a club
    /// account. Without the section: no platform (games that need a club account say so on the lock screen).
    /// </summary>
    private void WriteAccountPoolSettings()
    {
        var path = Path.Combine(AgentDir, AgentFiles.AccountPoolFile);
        try
        {
            var platforms = new Dictionary<string, PlatformLogin>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _config.GetSection("AccountPool:Platforms").GetChildren())
            {
                var login = new PlatformLogin { ClientPath = p["ClientPath"], LoginArguments = p["LoginArguments"] };
                if (AccountLogin.IsUsable(login)) platforms[p.Key.ToLowerInvariant()] = login;
                else _logger.LogWarning("AccountPool: the platform {Platform} is ignored (ClientPath must be an absolute .exe path, LoginArguments must contain {{login}})", p.Key);
            }
            WriteAtomic(path, new AccountPoolSettings { Platforms = platforms }.ToJson());
            _logger.LogInformation("Club accounts: {Count} platform(s) can log in on this PC", platforms.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write {Path}; games that need a club account cannot log in", path);
        }
    }

    // ──── COMMANDS ────

    private async Task PollAndExecuteAsync(HttpClient http, CancellationToken ct)
    {
        if (_deviceId == Guid.Empty || string.IsNullOrWhiteSpace(_deviceKey)) return;

        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"api/devices/{_deviceId}/commands?max=5");
        pollReq.Headers.Add("X-Device-Key", _deviceKey);
        using var pollResp = await http.SendAsync(pollReq, ct);
        if (pollResp.StatusCode == HttpStatusCode.Forbidden)
        {
            // Not approved (any more); the next heartbeat tells the status
            SetEnrollmentStatus("pending");
            return;
        }
        if (pollResp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _nextHeartbeatUtc = DateTimeOffset.UtcNow; // the heartbeat handles re-registration
            return;
        }
        pollResp.EnsureSuccessStatusCode();

        var cmds = await pollResp.Content.ReadFromJsonAsync<List<CommandView>>(JsonOptions, ct) ?? new();
        foreach (var cmd in cmds)
        {
            string status = "done";
            string? result = null;
            try
            {
                switch (cmd.Type?.ToLowerInvariant())
                {
                    case CommandTypes.Restart:
                        ExecuteRestart();
                        break;
                    case CommandTypes.Shutdown:
                        ExecuteShutdown();
                        break;
                    case CommandTypes.Lock:
                        ApplyLock();
                        break;
                    case CommandTypes.Unlock:
                        ApplyUnlock(cmd.Payload);
                        break;
                    case CommandTypes.Logoff:
                        result = ForwardLogoff();
                        break;
                    case CommandTypes.Message:
                        _logger.LogInformation("MESSAGE: {Text}", cmd.Payload?.ToString() ?? "");
                        break;
                    case CommandTypes.SessionSet:
                        HandleSessionSet(cmd.Payload);
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
            WriteLocalState();

            var ack = new AckCommandRequest { Status = status, Result = result };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/commands/{cmd.Id}/ack");
            req.Headers.Add("X-Device-Key", _deviceKey);
            req.Content = JsonContent.Create(ack);
            try
            {
                using var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Also a timeout: the next commands are still run and acknowledged
                _logger.LogWarning("Failed to ACK command {CommandId}: {Error}", cmd.Id, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// {"endUtc": "..."} sets the session end; {"endUtc": null} clears it (open-ended session, or a
    /// paused one: the countdown stops). It does not unlock: the server sends "unlock" after it at a
    /// session start, a resume or a move, and "lock" at the end and at a pause.
    /// </summary>
    private void HandleSessionSet(object? payload)
    {
        try
        {
            if (TryReadEndUtc(payload, out var endUtc))
            {
                lock (_gate) _lock = _lock.SessionSet(endUtc);
                _logger.LogInformation("Session end set to {End}", endUtc?.ToString("O") ?? "none (open-ended or paused)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse session_set payload");
        }
    }

    /// <summary>True when the payload has "endUtc": a time, or null (no end time).</summary>
    private static bool TryReadEndUtc(object? payload, out DateTimeOffset? endUtc)
    {
        endUtc = null;
        if (payload is JsonElement element) return TryReadEndUtc(element, out endUtc);

        var json = payload?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryReadEndUtc(doc.RootElement, out endUtc);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadEndUtc(JsonElement element, out DateTimeOffset? endUtc)
    {
        endUtc = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("endUtc", out var end)) return false;
        if (end.ValueKind == JsonValueKind.Null) return true;
        if (end.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(end.GetString(), out var parsed))
        {
            endUtc = parsed;
            return true;
        }
        return false;
    }

    private void ExecuteRestart()
    {
        var psi = new ProcessStartInfo("shutdown", "/r /t 5")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
        _logger.LogInformation("Restart scheduled in 5 seconds");
    }

    private void ExecuteShutdown()
    {
        var psi = new ProcessStartInfo("shutdown", "/s /t 5")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
        _logger.LogInformation("Shutdown scheduled in 5 seconds (Wake-on-LAN starts the PC again)");
    }

    /// <summary>
    /// The agent is a service in session 0 (LocalService): "shutdown /l" there logs off nothing in the player's
    /// session. The lock screen runs in that session, so the agent hands the request to it through state.json
    /// (for one minute); the lock screen logs the session off once.
    /// </summary>
    private string ForwardLogoff()
    {
        lock (_gate) _logoff = new LogoffRequest { Id = Guid.NewGuid(), RequestedUtc = DateTimeOffset.UtcNow };
        _logger.LogInformation("Log off: handed to the lock screen of the player's session");
        return "Sent to the lock screen: it logs off the player's Windows session";
    }

    private void ApplyLock()
    {
        long generation;
        lock (_gate)
        {
            _lock = _lock.Lock();
            generation = _lock.Generation;
        }
        _logger.LogInformation("Locked by the server (lock {Generation}; a staff PIN unlock on the lock screen ends)", generation);
    }

    /// <summary>No payload: unlock for a session. {"reason":"staff"}: staff unlock without a session (until the next agent start).</summary>
    private void ApplyUnlock(object? payload)
    {
        var reason = ReadUnlockReason(payload);
        AgentLock state;
        lock (_gate) state = _lock = _lock.UnlockFor(reason);
        _logger.LogInformation(state.Unlock == AgentLock.UnlockStaff
            ? "Unlocked by staff without a session (until a lock command or the next start of the agent)"
            : "Unlocked for the session");
    }

    private static string? ReadUnlockReason(object? payload)
    {
        try
        {
            using var doc = payload switch
            {
                JsonElement e => JsonDocument.Parse(e.GetRawText()),
                string s when !string.IsNullOrWhiteSpace(s) => JsonDocument.Parse(s),
                _ => null
            };
            if (doc?.RootElement is { ValueKind: JsonValueKind.Object } root &&
                root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                return reason.GetString();
        }
        catch (JsonException)
        {
            // not JSON: an unlock for a session
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>state.json, read by Cms.Launcher. LockChangedUtc changes whenever IsLocked changes.</summary>
    private sealed record AgentState(
        bool IsLocked,
        long RemainingSeconds,
        DateTimeOffset? SessionEndUtc,
        string LockReason,
        string? ServerUrl,
        DateTimeOffset? LockChangedUtc,
        string? Enrollment)
    {
        /// <summary>Why the PC is unlocked: "session", "staff" or "none" (Cms.Client.AgentLock). null = file of an older agent.</summary>
        public string? Unlock { get; init; }
        /// <summary>
        /// The number of lock commands (Cms.Client.AgentLock.Generation): it changes with every lock command, also on a locked
        /// PC, so the lock screen ends a local staff PIN unlock at the next lock command.
        /// </summary>
        public long LockGeneration { get; init; }
        /// <summary>Process id of the agent: the lock screen talks only to a pipe server with this id.</summary>
        public int AgentPid { get; init; }
        /// <summary>Active session from the last heartbeat (player, tariff, planned end), for display only.</summary>
        public ClientSessionInfo? Session { get; init; }
        /// <summary>A "log off" of the staff for the lock screen (one minute).</summary>
        public LogoffRequest? Logoff { get; init; }
        /// <summary>The reservation that holds the PC now ("Reserved for ... at HH:mm").</summary>
        public ClientReservationInfo? Reservation { get; init; }
    }

    private sealed record LauncherConfig(string ServerUrl);

    private sealed class RegistrationRefusedException(string message) : Exception(message);

    private sealed class DeviceCredentials
    {
        public Guid DeviceId { get; set; }
        public string DeviceKey { get; set; } = string.Empty;
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
        public string Status { get; set; } = "approved";
    }

    private sealed class DeviceHeartbeatRequest
    {
        public double CpuPercent { get; set; }
        public double MemPercent { get; set; }
        public string? ActiveUser { get; set; }
        public string Ip { get; set; } = string.Empty;
        public TimeSpan Uptime { get; set; }
        public string? Mac { get; set; }
    }
}
