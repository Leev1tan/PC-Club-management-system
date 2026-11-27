using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cms.Shared;

namespace Cms.Client;

/// <summary>A network card of the PC: MAC address (any format), IPv4 addresses, state.</summary>
public sealed record NicInfo(string Mac, IReadOnlyList<string> Addresses, bool Up, bool Loopback);

public static class NetworkIdentity
{
    /// <summary>
    /// The MAC address for Wake-on-LAN: of the card that has the local IP of the agent (the one that
    /// reaches the server), else of the first running card with a valid MAC; normalized, or null.
    /// </summary>
    public static string? PickMac(IEnumerable<NicInfo> nics, string? localIp)
    {
        var usable = nics.Where(n => n.Up && !n.Loopback && MacAddresses.Normalize(n.Mac) != null).ToList();
        var match = !string.IsNullOrEmpty(localIp) ? usable.FirstOrDefault(n => n.Addresses.Contains(localIp)) : null;
        return MacAddresses.Normalize((match ?? usable.FirstOrDefault(n => n.Addresses.Count > 0) ?? usable.FirstOrDefault())?.Mac);
    }

    /// <summary>The network cards of this PC.</summary>
    public static IReadOnlyList<NicInfo> Current()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Select(n => new NicInfo(
                n.GetPhysicalAddress().ToString(),
                n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList(),
                n.OperationalStatus == OperationalStatus.Up,
                n.NetworkInterfaceType == NetworkInterfaceType.Loopback)).ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NicInfo>();
        }
    }
}
