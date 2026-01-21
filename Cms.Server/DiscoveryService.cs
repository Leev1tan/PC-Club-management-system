using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cms.Server;

/// <summary>
/// Background service that listens for UDP discovery broadcasts from agents.
/// When an agent broadcasts "CMS_DISCOVER" on port 5082, the server responds
/// with "CMS_SERVER:http://{serverIP}:5081" so the agent can auto-configure.
/// </summary>
public class DiscoveryService : BackgroundService
{
    private readonly ILogger<DiscoveryService> _logger;
    private readonly IConfiguration _config;
    private const int DiscoveryPort = 5082;
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

        _logger.LogInformation("Starting UDP discovery listener on port {Port}", DiscoveryPort);

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                var message = Encoding.UTF8.GetString(result.Buffer);

                if (message.Trim() == DiscoveryRequest)
                {
                    _logger.LogInformation("Discovery request from {RemoteEndPoint}", result.RemoteEndPoint);

                    var serverUrl = GetServerUrl();
                    var response = Encoding.UTF8.GetBytes($"{ResponsePrefix}{serverUrl}");
                    await udp.SendAsync(response, response.Length, result.RemoteEndPoint);

                    _logger.LogInformation("Sent discovery response: {ServerUrl}", serverUrl);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in discovery listener");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("UDP discovery listener stopped");
    }

    private string GetServerUrl()
    {
        // First check config for explicit URL
        var configUrl = _config["Discovery:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(configUrl))
            return configUrl;

        // Auto-detect: get first non-loopback IPv4 address
        var port = _config.GetValue<int>("Discovery:HttpPort", 5081);
        var host = GetLocalIPAddress() ?? "localhost";
        return $"http://{host}:{port}";
    }

    private static string? GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            // Connect to a public IP (doesn't actually send data) to determine local interface
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString();
        }
        catch
        {
            // Fallback: enumerate network interfaces
            var hostName = Dns.GetHostName();
            var addresses = Dns.GetHostAddresses(hostName);
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    return addr.ToString();
            }
            return null;
        }
    }
}
