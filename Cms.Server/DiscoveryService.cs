using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Cms.Server;

/// <summary>
/// Background service that listens for UDP discovery broadcasts from agents.
/// When an agent broadcasts "CMS_DISCOVER" on port 5082, the server responds
/// with "CMS_SERVER:http://{serverIP}:5081" so the agent can auto-configure.
/// The address is the one of the network card that the request came in on (Cms.Server.DiscoveryAddress), not the card
/// of the route to the internet: a server with two cards answered with the wrong address before.
/// </summary>
public class DiscoveryService : BackgroundService
{
    private readonly ILogger<DiscoveryService> _logger;
    private readonly IConfiguration _config;
    private const int DefaultDiscoveryPort = 5082;
    private const string DiscoveryRequest = "CMS_DISCOVER";
    private const string ResponsePrefix = "CMS_SERVER:";

    public DiscoveryService(ILogger<DiscoveryService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config.GetValue<bool>("Discovery:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("UDP discovery is disabled");
            return;
        }

        var discoveryPort = _config.GetValue<int>("Discovery:Port", DefaultDiscoveryPort);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(discoveryPort, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // For example the port is in use: the server goes on without discovery and tries again later
                _logger.LogError(ex, "UDP discovery on port {Port} failed; trying again in 30 s", discoveryPort);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("UDP discovery listener stopped");
    }

    private async Task ListenAsync(int discoveryPort, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting UDP discovery listener on port {Port}", discoveryPort);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true); // the card of each request
        socket.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
        var buffer = new byte[512];

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stoppingToken);
                var message = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                if (message.Trim() != DiscoveryRequest || result.RemoteEndPoint is not IPEndPoint remote) continue;

                _logger.LogInformation("Discovery request from {RemoteEndPoint} (network card {Interface})", remote, result.PacketInformation.Interface);
                var serverUrl = GetServerUrl(remote.Address, result.PacketInformation.Interface);
                var response = Encoding.UTF8.GetBytes($"{ResponsePrefix}{serverUrl}");
                await socket.SendToAsync(response, SocketFlags.None, remote, stoppingToken);
                _logger.LogInformation("Sent discovery response: {ServerUrl}", serverUrl);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Discovery: {Message}", ex.Message);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private string GetServerUrl(IPAddress requester, int interfaceIndex)
    {
        // First check config for explicit URL
        var configUrl = _config["Discovery:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(configUrl))
            return configUrl;

        var port = _config.GetValue<int>("Discovery:HttpPort", 5081);
        var host = DiscoveryAddress.Pick(requester, interfaceIndex > 0 ? interfaceIndex : null, DiscoveryAddress.LocalAddresses())?.ToString()
                   ?? GetRouteAddress() ?? "localhost";
        return $"http://{host}:{port}";
    }

    /// <summary>The last fallback: the address of the route to the internet (one network card).</summary>
    private static string? GetRouteAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

/// <summary>A local IPv4 address of the server, its subnet mask, and the index of its network card.</summary>
public sealed record LocalAddress(int InterfaceIndex, IPAddress Address, IPAddress Mask);

/// <summary>
/// The address of the discovery answer: the address of the network card that the request came in on, the one on the
/// subnet of the agent when the card has more than one; without the card, the local address on the subnet of the agent.
/// Null when nothing fits (the caller uses the route to the internet). Without I/O, so it is tested on any OS.
/// </summary>
public static class DiscoveryAddress
{
    public static IPAddress? Pick(IPAddress requester, int? interfaceIndex, IReadOnlyList<LocalAddress> locals)
    {
        if (requester.IsIPv4MappedToIPv6) requester = requester.MapToIPv4();
        var usable = locals.Where(l => l.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(l.Address) &&
                                       !l.Address.Equals(IPAddress.Any)).ToList();
        bool OnRequesterSubnet(LocalAddress l) => InSubnet(requester, l.Address, l.Mask);
        if (interfaceIndex is { } index)
        {
            var onCard = usable.Where(l => l.InterfaceIndex == index).ToList();
            var best = onCard.FirstOrDefault(OnRequesterSubnet) ?? onCard.FirstOrDefault(l => !IsLinkLocal(l.Address)) ?? onCard.FirstOrDefault();
            if (best != null) return best.Address;
        }
        return usable.FirstOrDefault(OnRequesterSubnet)?.Address;
    }

    /// <summary>True when both addresses are in the same IPv4 subnet of the mask.</summary>
    public static bool InSubnet(IPAddress address, IPAddress local, IPAddress mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || local.AddressFamily != AddressFamily.InterNetwork ||
            mask.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = address.GetAddressBytes();
        var b = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (m.All(x => x == 0)) return false;
        for (var i = 0; i < 4; i++)
            if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        return true;
    }

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    /// <summary>The IPv4 addresses of the network cards that are up.</summary>
    public static IReadOnlyList<LocalAddress> LocalAddresses()
    {
        var list = new List<LocalAddress>();
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var properties = card.GetIPProperties();
                    var index = properties.GetIPv4Properties()?.Index ?? -1;
                    foreach (var unicast in properties.UnicastAddresses)
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask != null)
                            list.Add(new LocalAddress(index, unicast.Address, unicast.IPv4Mask));
                }
                catch (NetworkInformationException)
                {
                    // a card without IPv4: skipped
                }
                catch (PlatformNotSupportedException)
                {
                    // not on this OS: skipped
                }
            }
        }
        catch (NetworkInformationException)
        {
            // no list of cards: the caller falls back
        }
        return list;
    }
}
