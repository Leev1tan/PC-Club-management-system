using System.Security.Cryptography;

namespace Cms.Shared;

/// <summary>
/// Reads the club logo that the agent wrote (%ProgramData%\ClubAgent\club-logo.img) for the lock screen. The bytes are
/// trusted only when their SHA-256 is the hash that the server sent in the client settings, they are at most 512 KB and
/// they start like a PNG or a JPEG. Anything else (a missing, half-written, old or planted file) gives null, and the lock
/// screen shows the club name text instead. Byte checks only: no image types here, so this runs in tests on Linux.
/// </summary>
public static class LogoLoader
{
    public const int MaxBytes = 512 * 1024;

    public static byte[]? ReadTrusted(string path, string expectedSha256, int maxBytes = MaxBytes)
    {
        try
        {
            if (!ClientSettings.IsSha256Hex(expectedSha256)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes) return null;
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[Math.Min(stream.Length, maxBytes + 1L)];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != bytes.Length || stream.ReadByte() != -1) return null; // the file changed while it was read
            }
            return Check(bytes, expectedSha256, maxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The bytes when they are a PNG or JPEG of at most maxBytes with this SHA-256; else null.</summary>
    public static byte[]? Check(byte[] bytes, string expectedSha256, int maxBytes = MaxBytes)
    {
        if (bytes.Length == 0 || bytes.Length > maxBytes || !IsPngOrJpeg(bytes) || !ClientSettings.IsSha256Hex(expectedSha256)) return null;
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        return string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;
    }

    /// <summary>
    /// The pixel size from the PNG header (IHDR) or the first JPEG frame header (SOFn); false for other bytes or a size
    /// of 0. The lock screen uses it to decode a logo at a bounded size.
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length >= 24 && b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && b.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[16..]), int.MaxValue);
            height = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[20..]), int.MaxValue);
        }
        else if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var pos = 2;
            while (pos + 4 <= b.Length && b[pos] == 0xFF)
            {
                var marker = b[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    if (pos + 9 > b.Length) return false;
                    height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 5)..]);
                    width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 7)..]);
                    break;
                }
                if (length < 2 || marker is 0xDA or 0xD9) return false;
                pos += 2 + length;
            }
        }
        return width > 0 && height > 0;
    }

    public static bool IsPngOrJpeg(ReadOnlySpan<byte> b) =>
        b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) || (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF);
}
