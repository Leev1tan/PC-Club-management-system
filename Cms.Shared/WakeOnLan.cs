using System.Globalization;

namespace Cms.Shared;

/// <summary>MAC addresses that the agent reports and the server stores for Wake-on-LAN.</summary>
public static class MacAddresses
{
    /// <summary>
    /// "AA:BB:CC:DD:EE:FF" for six bytes written with ':', '-' or nothing between them; null for anything
    /// else, and for addresses that cannot wake a PC: all zeros, broadcast, or multicast (lowest bit of the
    /// first byte set).
    /// </summary>
    public static string? Normalize(string? text)
    {
        var bytes = Parse(text);
        return bytes == null ? null : string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    public static byte[]? Parse(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        string hex;
        if (t.Length == 17 && (t.Count(c => c == ':') == 5 || t.Count(c => c == '-') == 5))
        {
            var sep = t[2];
            for (var i = 2; i < 17; i += 3) if (t[i] != sep) return null;
            hex = t.Replace(sep.ToString(), "");
        }
        else if (t.Length == 12) hex = t;
        else return null;
        if (!hex.All(Uri.IsHexDigit)) return null;
        var bytes = Convert.FromHexString(hex);
        if (bytes.All(b => b == 0) || bytes.All(b => b == 0xFF) || (bytes[0] & 1) == 1) return null;
        return bytes;
    }
}

public static class WakeOnLan
{
    public const int DefaultPort = 9;

    /// <summary>The magic packet: 6 bytes 0xFF, then the MAC address 16 times (102 bytes).</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("A MAC address has 6 bytes.", nameof(mac));
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }
}
