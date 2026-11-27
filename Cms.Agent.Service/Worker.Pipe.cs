using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Cms.Client;
using Microsoft.Win32.SafeHandles;

namespace Cms.Agent.Service;

/// <summary>
/// The pipe of the lock screen (Cms.Client.AgentPipe): the launcher runs as the player and cannot read the
/// device key, so it asks the agent, and the agent asks the server with the key of this PC. Only these requests
/// exist: the offers of the zone, the player login, the end of the player's session, the bar menu, a bar order,
/// the player's cancel of a new order, and a club account for a game.
/// The secret of a login goes to the server only; it is never logged or written to disk. The credentials of a club
/// account go only to the installed lock screen (the program of the pipe client is checked), in memory, never logged.
/// </summary>
public partial class Worker
{
    private static readonly TimeSpan PipeServerTimeout = TimeSpan.FromSeconds(15);
    /// <summary>The installed lock screen; only this program gets club account credentials (AccountPool:LauncherPath).</summary>
    private const string DefaultLauncherPath = @"C:\ClubLauncher\Cms.Launcher.exe";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    private async Task RunLauncherPipeAsync(CancellationToken ct)
    {
        try
        {
            var server = new AgentPipeServer(CreatePipe, HandlePipeAsync, ex => _logger.LogDebug(ex, "Launcher pipe"), AuthorizePipeClient);
            await server.RunAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The launcher pipe stopped; player login on the lock screen is not possible");
        }
    }

    /// <summary>
    /// Players (Users) may read and write the pipe but not create an instance of it, so nobody can take
    /// the name while the agent runs; the first instance is created with FirstPipeInstance.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } self)
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(AgentPipe.Name, PipeDirection.InOut, AgentPipe.MaxConnections, PipeTransmissionMode.Byte,
            options, AgentPipe.MaxMessageBytes, AgentPipe.MaxMessageBytes, security);
    }

    /// <summary>
    /// A club account request must come from the installed lock screen: the program of the pipe client (its process id from
    /// the pipe) must be AccountPool:LauncherPath. Every other request is allowed (the players may use the pipe).
    /// </summary>
    private bool AuthorizePipeClient(NamedPipeServerStream stream, PipeRequest req)
    {
        if (req.Op != PipeOps.Account) return true;
        var launcher = _config["AccountPool:LauncherPath"];
        if (string.IsNullOrWhiteSpace(launcher)) launcher = DefaultLauncherPath;
        string? path = null;
        if (GetNamedPipeClientProcessId(stream.SafePipeHandle, out var pid))
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                path = null; // the client went away, or its program cannot be read: refused
            }
        }
        var ok = AgentPipeClient.IsTrustedLauncher(path, launcher);
        if (!ok) _logger.LogWarning("Lock screen pipe: a club account request from {Path} refused (only {Launcher} may ask)", path ?? "?", launcher);
        return ok;
    }

    private sealed record LeaseAnswer(long LeaseId, string Platform, string LoginMask, bool Delivered);
    private sealed record CredentialAnswer(long LeaseId, string Platform, string Login, string Secret);

    /// <summary>
    /// A club account for a game: the lease of this PC (the server picks a free account of the platform), then, the first
    /// time only, its credentials. They go to the lock screen in the answer and nowhere else (no log, no file).
    /// </summary>
    private async Task<PipeResponse> HandleAccountAsync(HttpClient http, PipeRequest req, CancellationToken ct)
    {
        if (req.GameId is not { } gameId || gameId == Guid.Empty) return PipeResponse.Error(400, "bad_request");
        using var leaseMessage = new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/game-account") { Content = JsonContent.Create(new { gameId }) };
        leaseMessage.Headers.Add("X-Device-Key", _deviceKey);
        using var leaseResponse = await http.SendAsync(leaseMessage, ct);
        var leaseBody = await leaseResponse.Content.ReadAsStringAsync(ct);
        if (!leaseResponse.IsSuccessStatusCode)
        {
            _logger.LogInformation("Lock screen: club account refused by the server ({Status})", (int)leaseResponse.StatusCode);
            return new PipeResponse((int)leaseResponse.StatusCode, string.IsNullOrWhiteSpace(leaseBody) ? null : leaseBody);
        }
        var lease = JsonSerializer.Deserialize<LeaseAnswer>(leaseBody, JsonOptions);
        if (lease == null) return PipeResponse.Error(502, "bad_answer");
        if (lease.Delivered)
            return new PipeResponse(200, JsonSerializer.Serialize(new AccountLease(lease.LeaseId, lease.Platform, lease.LoginMask, true), JsonOptions));

        using var credentialMessage = new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/game-account/{lease.LeaseId}/credential") { Content = JsonContent.Create(new { }) };
        credentialMessage.Headers.Add("X-Device-Key", _deviceKey);
        using var credentialResponse = await http.SendAsync(credentialMessage, ct);
        var credentialBody = await credentialResponse.Content.ReadAsStringAsync(ct);
        if (!credentialResponse.IsSuccessStatusCode)
        {
            _logger.LogInformation("Lock screen: the credentials of club account {Mask} refused by the server ({Status})", lease.LoginMask, (int)credentialResponse.StatusCode);
            return new PipeResponse((int)credentialResponse.StatusCode, string.IsNullOrWhiteSpace(credentialBody) ? null : credentialBody);
        }
        var credential = JsonSerializer.Deserialize<CredentialAnswer>(credentialBody, JsonOptions);
        if (credential == null) return PipeResponse.Error(502, "bad_answer");
        _logger.LogInformation("Lock screen: club account {Mask} ({Platform}) handed to the lock screen", lease.LoginMask, lease.Platform);
        return new PipeResponse(200, JsonSerializer.Serialize(new AccountLease(lease.LeaseId, lease.Platform, lease.LoginMask, false, credential.Login, credential.Secret), JsonOptions));
    }

    private async Task<PipeResponse> HandlePipeAsync(PipeRequest req, CancellationToken ct)
    {
        string? serverUrl;
        lock (_gate) serverUrl = _serverUrl;
        if (_deviceId == Guid.Empty || string.IsNullOrWhiteSpace(_deviceKey) || _enrollmentStatus != "approved" || string.IsNullOrWhiteSpace(serverUrl))
            return PipeResponse.Error(503, "agent_not_ready");

        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        http.Timeout = PipeServerTimeout;
        if (req.Op == PipeOps.Account)
        {
            try
            {
                return await HandleAccountAsync(http, req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return PipeResponse.Error((int)HttpStatusCode.ServiceUnavailable, "server_unreachable");
            }
        }
        using var message = req.Op switch
        {
            PipeOps.Offers => new HttpRequestMessage(HttpMethod.Get, $"api/devices/{_deviceId}/login-offers"),
            PipeOps.Login => new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/player-login")
            {
                Content = JsonContent.Create(new { username = req.Username, secret = req.Secret, offer = req.Offer, minutes = req.Minutes, packageId = req.PackageId })
            },
            PipeOps.Menu => new HttpRequestMessage(HttpMethod.Get, $"api/devices/{_deviceId}/bar-menu"),
            PipeOps.Order => new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/orders")
            {
                Content = JsonContent.Create(new
                {
                    items = (req.Items ?? Array.Empty<PipeOrderItem>()).Take(20).Select(i => new { productId = i.ProductId, quantity = i.Quantity }),
                    payment = req.Payment
                })
            },
            PipeOps.OrderCancel => new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/orders/cancel") { Content = JsonContent.Create(new { }) },
            _ => new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/player-logout") { Content = JsonContent.Create(new { }) }
        };
        message.Headers.Add("X-Device-Key", _deviceKey);
        try
        {
            using var resp = await http.SendAsync(message, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode && req.Op is not (PipeOps.Offers or PipeOps.Menu))
            {
                // The session changed: the next heartbeat brings it to the lock screen at once
                _nextHeartbeatUtc = DateTimeOffset.UtcNow;
                _logger.LogInformation("Lock screen: {Op} done", req.Op);
            }
            else if (!resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Lock screen: {Op} refused by the server ({Status})", req.Op, (int)resp.StatusCode);
            }
            return new PipeResponse((int)resp.StatusCode, string.IsNullOrWhiteSpace(body) ? null : body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return PipeResponse.Error((int)HttpStatusCode.ServiceUnavailable, "server_unreachable");
        }
    }
}
