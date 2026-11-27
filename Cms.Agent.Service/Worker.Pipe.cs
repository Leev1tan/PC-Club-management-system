using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Cms.Client;

namespace Cms.Agent.Service;

/// <summary>
/// The pipe of the lock screen (Cms.Client.AgentPipe): the launcher runs as the player and cannot read the
/// device key, so it asks the agent, and the agent asks the server with the key of this PC. Only three
/// requests exist: the offers of the zone, the player login and the end of the player's session.
/// The secret of a login goes to the server only; it is never logged or written to disk.
/// </summary>
public partial class Worker
{
    private static readonly TimeSpan PipeServerTimeout = TimeSpan.FromSeconds(15);

    private async Task RunLauncherPipeAsync(CancellationToken ct)
    {
        try
        {
            var server = new AgentPipeServer(CreatePipe, HandlePipeAsync, ex => _logger.LogDebug(ex, "Launcher pipe"));
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

    private async Task<PipeResponse> HandlePipeAsync(PipeRequest req, CancellationToken ct)
    {
        string? serverUrl;
        lock (_gate) serverUrl = _serverUrl;
        if (_deviceId == Guid.Empty || string.IsNullOrWhiteSpace(_deviceKey) || _enrollmentStatus != "approved" || string.IsNullOrWhiteSpace(serverUrl))
            return PipeResponse.Error(503, "agent_not_ready");

        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        http.Timeout = PipeServerTimeout;
        using var message = req.Op switch
        {
            PipeOps.Offers => new HttpRequestMessage(HttpMethod.Get, $"api/devices/{_deviceId}/login-offers"),
            PipeOps.Login => new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/player-login")
            {
                Content = JsonContent.Create(new { username = req.Username, secret = req.Secret, offer = req.Offer, minutes = req.Minutes, packageId = req.PackageId })
            },
            _ => new HttpRequestMessage(HttpMethod.Post, $"api/devices/{_deviceId}/player-logout") { Content = JsonContent.Create(new { }) }
        };
        message.Headers.Add("X-Device-Key", _deviceKey);
        try
        {
            using var resp = await http.SendAsync(message, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode && req.Op != PipeOps.Offers)
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
