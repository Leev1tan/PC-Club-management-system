using System.Net;
using System.Net.Sockets;
using Cms.Shared;

namespace Cms.Server.Devices;

/// <summary>
/// Wake-on-LAN settings (spec area 4): the server sends the magic packet to the MAC address that the agent
/// reports in its heartbeat.
///   Power:WakeBroadcast (default 255.255.255.255) - IPv4 address to send to: the broadcast of the club LAN
///                         (for example 10.3.3.255) or a WoL relay of the router
///   Power:WakePort      (default 9)               - UDP port (7 or 9)
/// </summary>
public sealed class PowerOptions
{
    public IPAddress Broadcast { get; init; } = IPAddress.Broadcast;
    public int Port { get; init; } = WakeOnLan.DefaultPort;
    /// <summary>Set when the configuration is not valid: Wake-on-LAN is refused (wake_not_configured).</summary>
    public string? Error { get; init; }

    public IPEndPoint Target => new(Broadcast, Port);

    public static PowerOptions FromConfiguration(IConfiguration config)
    {
        var text = (config["Power:WakeBroadcast"] ?? "255.255.255.255").Trim();
        var portText = config["Power:WakePort"];
        var port = WakeOnLan.DefaultPort;
        if (!string.IsNullOrWhiteSpace(portText) && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            return new PowerOptions { Error = $"Power:WakePort must be a UDP port (1-65535), not '{portText}'." };
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return new PowerOptions { Error = $"Power:WakeBroadcast must be an IPv4 address, not '{text}'." };
        return new PowerOptions { Broadcast = ip, Port = port };
    }
}

/// <summary>Sends the Wake-on-LAN packet (UDP in a club; simulated in the demo).</summary>
public interface IWakeOnLanSender
{
    /// <summary>True in the demo: nothing goes to the network (the audit text says so).</summary>
    bool Simulated => false;

    Task SendAsync(Guid deviceId, byte[] mac, IPEndPoint target, CancellationToken ct = default);
}

/// <summary>Three magic packets 100 ms apart (a packet can be lost), from the server to the target address.</summary>
public sealed class UdpWakeOnLanSender : IWakeOnLanSender
{
    public async Task SendAsync(Guid deviceId, byte[] mac, IPEndPoint target, CancellationToken ct = default)
    {
        var packet = WakeOnLan.MagicPacket(mac);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        for (var i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, target, ct);
            if (i < 2) await Task.Delay(100, ct);
        }
    }
}
