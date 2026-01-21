using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cms.Agent.Service;

/// <summary>
/// Discovers the CMS server on the local network via UDP broadcast.
/// Sends "CMS_DISCOVER" to broadcast address on port 5082 and waits for
/// "CMS_SERVER:{url}" response from the server.
/// </summary>
public static class ServerDiscovery
{
    private const int DiscoveryPort = 5082;
    private const string DiscoveryRequest = "CMS_DISCOVER";
    private const string ResponsePrefix = "CMS_SERVER:";

    /// <summary>
    /// Attempts to discover the server URL via UDP broadcast.
    /// </summary>
    /// <param name="timeout">How long to wait for a response</param>
    /// <param name="logger">Optional logger</param>
    /// <returns>Server URL if found, null otherwise</returns>
    public static async Task<string?> DiscoverServerAsync(TimeSpan timeout, ILogger? logger = null)
    {
        logger?.LogInformation("Attempting to discover server via UDP broadcast...");

        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;

            var requestBytes = Encoding.UTF8.GetBytes(DiscoveryRequest);
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

            // Send discovery broadcast
            await udp.SendAsync(requestBytes, requestBytes.Length, broadcastEndpoint);
            logger?.LogDebug("Sent discovery broadcast to {Endpoint}", broadcastEndpoint);

            // Wait for response with timeout
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                var response = Encoding.UTF8.GetString(result.Buffer);

                if (response.StartsWith(ResponsePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var serverUrl = response.Substring(ResponsePrefix.Length).Trim();
                    logger?.LogInformation("Discovered server at {ServerUrl} from {RemoteEndPoint}", 
                        serverUrl, result.RemoteEndPoint);
                    return serverUrl;
                }

                logger?.LogWarning("Received unexpected response: {Response}", response);
            }
            catch (OperationCanceledException)
            {
                logger?.LogDebug("Discovery timed out after {Timeout}", timeout);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Discovery failed");
        }

        return null;
    }

    /// <summary>
    /// Attempts discovery multiple times with increasing timeouts.
    /// </summary>
    public static async Task<string?> DiscoverServerWithRetryAsync(int maxAttempts, ILogger? logger = null)
    {
        var baseTimeout = TimeSpan.FromSeconds(2);

        for (int i = 0; i < maxAttempts; i++)
        {
            var timeout = TimeSpan.FromSeconds(baseTimeout.TotalSeconds * (i + 1));
            logger?.LogInformation("Discovery attempt {Attempt}/{MaxAttempts} (timeout: {Timeout}s)", 
                i + 1, maxAttempts, (int)timeout.TotalSeconds);

            var serverUrl = await DiscoverServerAsync(timeout, logger);
            if (serverUrl != null)
                return serverUrl;
        }

        logger?.LogWarning("Server discovery failed after {MaxAttempts} attempts", maxAttempts);
        return null;
    }
}
