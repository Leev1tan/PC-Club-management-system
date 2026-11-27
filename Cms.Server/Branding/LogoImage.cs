using System.Buffers.Binary;

namespace Cms.Server.Branding;

/// <summary>Result of <see cref="LogoImage.Inspect"/>. Error: "type", "size" or "dimensions"; null when the logo is accepted.</summary>
public sealed record LogoCheck(bool Ok, string? ContentType, int Width, int Height, string? Error)
{
    public static LogoCheck Fail(string error, int width = 0, int height = 0) => new(false, null, width, height, error);
}

/// <summary>
/// Checks an uploaded club logo by its bytes only (the declared type and the file name are ignored): PNG or JPEG,
/// at most <see cref="MaxBytes"/>, at most <see cref="MaxSide"/> pixels wide and high. SVG, GIF, WebP and everything
/// else are refused. The structure is checked too, so a PNG or JPEG header in front of other content is refused:
/// a PNG must be a chain of chunks with correct CRCs from IHDR to IEND and nothing after it; a JPEG must have its
/// segments in order up to a frame header (SOFn) and end with the end marker (FF D9). The pixels are not decoded:
/// browsers and the lock screen decode them, and the lock screen shows the club name when that fails.
/// </summary>
public static class LogoImage
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxSide = 1024;

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static LogoCheck Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) return LogoCheck.Fail("size");
        if (bytes.StartsWith(PngSignature)) return InspectPng(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return InspectJpeg(bytes);
        return LogoCheck.Fail("type");
    }

    private static LogoCheck Sized(string type, int width, int height) =>
        width < 1 || height < 1 || width > MaxSide || height > MaxSide
            ? LogoCheck.Fail("dimensions", width, height)
            : new LogoCheck(true, type, width, height, null);

    private static LogoCheck InspectPng(ReadOnlySpan<byte> bytes)
    {
        var pos = PngSignature.Length;
        int width = 0, height = 0, chunks = 0;
        var idat = false;
        while (true)
        {
            if (bytes.Length - pos < 12) return LogoCheck.Fail("type");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[pos..]);
            if (length > (uint)(bytes.Length - pos - 12)) return LogoCheck.Fail("type");
            var typeAndData = bytes.Slice(pos + 4, 4 + (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(bytes[(pos + 8 + (int)length)..]);
            if (Crc32(typeAndData) != crc) return LogoCheck.Fail("type");
            var type = typeAndData[..4];
            var data = typeAndData[4..];
            pos += 12 + (int)length;
            chunks++;
            if (chunks == 1)
            {
                // IHDR first: width, height, bit depth, colour type, compression 0, filter 0, interlace 0 or 1
                if (!type.SequenceEqual("IHDR"u8) || data.Length != 13) return LogoCheck.Fail("type");
                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[4..]), int.MaxValue);
                if (!ValidPngFormat(data[8], data[9]) || data[10] != 0 || data[11] != 0 || data[12] > 1) return LogoCheck.Fail("type");
                continue;
            }
            if (type.SequenceEqual("IHDR"u8)) return LogoCheck.Fail("type");
            if (type.SequenceEqual("IDAT"u8)) idat = true;
            if (type.SequenceEqual("IEND"u8))
            {
                // Nothing may follow IEND: no other file glued to the image
                if (pos != bytes.Length || !idat) return LogoCheck.Fail("type");
                return Sized("image/png", width, height);
            }
        }
    }

    private static bool ValidPngFormat(byte bitDepth, byte colourType) => colourType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => false
    };

    private static LogoCheck InspectJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes[^2] != 0xFF || bytes[^1] != 0xD9) return LogoCheck.Fail("type");
        var pos = 2;
        while (pos < bytes.Length - 2)
        {
            if (bytes[pos] != 0xFF) return LogoCheck.Fail("type");
            var marker = bytes[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // fill byte
            pos += 2;
            // Markers without a length: TEM and RST0..RST7; SOI or EOI before a frame header is wrong
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            if (marker is 0xD8 or 0xD9 or 0x00 or 0xDA) return LogoCheck.Fail("type");
            if (bytes.Length - pos < 2) return LogoCheck.Fail("type");
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[pos..]);
            if (length < 2 || length > bytes.Length - pos) return LogoCheck.Fail("type");
            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC): precision, height, width
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8) return LogoCheck.Fail("type");
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(pos + 3)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(pos + 5)..]);
                return Sized("image/jpeg", width, height);
            }
            pos += length;
        }
        return LogoCheck.Fail("type");
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>CRC-32 of PNG chunks (ISO 3309, the same as zlib).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
