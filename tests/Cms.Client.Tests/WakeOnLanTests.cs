using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

public sealed class WakeOnLanTests
{
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:0f", "AA:BB:CC:DD:EE:0F")]
    [InlineData("AA-BB-CC-DD-EE-0F", "AA:BB:CC:DD:EE:0F")]
    [InlineData("aabbccddee0f", "AA:BB:CC:DD:EE:0F")]
    [InlineData(" 02:00:5E:10:00:01 ", "02:00:5E:10:00:01")]
    [InlineData("01:00:5E:00:00:01", null)] // multicast
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("FF:FF:FF:FF:FF:FF", null)]
    [InlineData("AA:BB-CC:DD:EE:0F", null)] // mixed separators
    [InlineData("AA:BB:CC:DD:EE", null)]
    [InlineData("gg:bb:cc:dd:ee:ff", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Mac_addresses_are_checked_and_normalized(string? text, string? expected) =>
        Assert.Equal(expected, MacAddresses.Normalize(text));

    [Fact]
    public void The_magic_packet_is_six_ff_and_sixteen_macs()
    {
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var packet = WakeOnLan.MagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(Enumerable.Repeat(mac, 16).SelectMany(m => m), packet[6..]);
        Assert.Throws<ArgumentException>(() => WakeOnLan.MagicPacket(new byte[5]));
    }

    [Fact]
    public void The_agent_reports_the_card_that_reaches_the_server()
    {
        var nics = new[]
        {
            new NicInfo("", new[] { "127.0.0.1" }, true, true),                 // loopback
            new NicInfo("00155D000001", new[] { "172.20.0.1" }, true, false),   // a virtual switch
            new NicInfo("D8BBC1000002", new[] { "10.3.3.105" }, true, false),   // the club LAN
            new NicInfo("D8BBC1000003", new[] { "192.168.88.2" }, false, false), // down
        };
        Assert.Equal("D8:BB:C1:00:00:02", NetworkIdentity.PickMac(nics, "10.3.3.105"));
        Assert.Equal("00:15:5D:00:00:01", NetworkIdentity.PickMac(nics, null)); // no local IP: the first running card
        Assert.Null(NetworkIdentity.PickMac(new[] { new NicInfo("", Array.Empty<string>(), true, true) }, "10.3.3.105"));
        Assert.NotNull(NetworkIdentity.Current()); // the real cards of this machine can be read
    }
}
