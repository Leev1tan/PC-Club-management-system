using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>
/// Known issue of the Windows test 2026-11-27: discovery answered with the address of the route to 8.8.8.8, wrong on a
/// server with two network cards. It now answers with the address of the card that the request came in on.
/// </summary>
public sealed class DiscoveryAddressTests
{
    private static readonly LocalAddress Lan = new(5, IPAddress.Parse("10.3.3.1"), IPAddress.Parse("255.255.255.0"));
    private static readonly LocalAddress Uplink = new(7, IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"));
    private static readonly LocalAddress Loop = new(1, IPAddress.Loopback, IPAddress.Parse("255.0.0.0"));
    private static readonly LocalAddress[] TwoCards = { Loop, Uplink, Lan };

    private static string? Pick(string requester, int? card, params LocalAddress[] locals) =>
        DiscoveryAddress.Pick(IPAddress.Parse(requester), card, locals.Length > 0 ? locals : TwoCards)?.ToString();

    [Fact]
    public void A_server_with_two_cards_answers_with_the_card_of_the_request()
    {
        Assert.Equal("10.3.3.1", Pick("10.3.3.150", 5));           // club LAN card
        Assert.Equal("192.168.1.20", Pick("192.168.1.77", 7));     // the other card
        Assert.Equal("10.3.3.1", Pick("10.3.4.20", 5));            // a routed club subnet on the LAN card
    }

    [Fact]
    public void Without_the_card_the_address_on_the_subnet_of_the_agent_is_used()
    {
        Assert.Equal("10.3.3.1", Pick("10.3.3.150", null));
        Assert.Equal("10.3.3.1", Pick("::ffff:10.3.3.150", null)); // IPv4 in IPv6 form
        Assert.Equal("10.3.3.1", Pick("10.3.3.150", 99));          // an unknown card index
        Assert.Null(Pick("172.16.0.5", null));                     // nothing fits: the caller uses the route
    }

    [Fact]
    public void On_a_card_with_two_addresses_the_one_on_the_subnet_of_the_agent_wins()
    {
        var second = new LocalAddress(5, IPAddress.Parse("10.9.9.1"), IPAddress.Parse("255.255.255.0"));
        var linkLocal = new LocalAddress(5, IPAddress.Parse("169.254.10.1"), IPAddress.Parse("255.255.0.0"));
        Assert.Equal("10.9.9.1", Pick("10.9.9.50", 5, linkLocal, Lan, second));
        Assert.Equal("10.3.3.1", Pick("10.3.3.150", 5, linkLocal, Lan, second));
        Assert.Equal("10.3.3.1", Pick("172.16.0.5", 5, linkLocal, Lan, second));  // no subnet match: not the link-local address
        Assert.Null(Pick("10.3.3.150", 1, Loop));                                  // loopback is never an answer
    }

    [Theory]
    [InlineData("10.3.3.150", "10.3.3.1", "255.255.255.0", true)]
    [InlineData("10.3.4.150", "10.3.3.1", "255.255.255.0", false)]
    [InlineData("10.3.4.150", "10.3.3.1", "255.255.0.0", true)]
    [InlineData("10.3.3.150", "10.3.3.1", "0.0.0.0", false)]
    public void Subnets_are_compared_with_the_mask(string address, string local, string mask, bool same) =>
        Assert.Equal(same, DiscoveryAddress.InSubnet(IPAddress.Parse(address), IPAddress.Parse(local), IPAddress.Parse(mask)));

    [Fact]
    public async Task The_service_answers_a_discovery_request_with_the_http_port()
    {
        var port = Random.Shared.Next(41000, 49000);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discovery:Enabled"] = "true", ["Discovery:Port"] = port.ToString(), ["Discovery:HttpPort"] = "5081"
        }).Build();
        using var service = new DiscoveryService(NullLogger<DiscoveryService>.Instance, config);
        using var stop = new CancellationTokenSource();
        await service.StartAsync(stop.Token);
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? answer = null;
            while (answer == null && !timeout.IsCancellationRequested)
            {
                await client.SendAsync(Encoding.UTF8.GetBytes("CMS_DISCOVER"), new IPEndPoint(IPAddress.Loopback, port));
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                wait.CancelAfter(TimeSpan.FromMilliseconds(500));
                try { answer = Encoding.UTF8.GetString((await client.ReceiveAsync(wait.Token)).Buffer); }
                catch (OperationCanceledException) { /* the listener starts: send again */ }
            }
            Assert.NotNull(answer);
            Assert.Matches(@"^CMS_SERVER:http://[^/]+:5081$", answer);
            Assert.DoesNotContain("127.0.0.1", answer); // loopback is never the answer
        }
        finally
        {
            stop.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }
}
