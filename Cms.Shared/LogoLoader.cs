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

    public static bool IsPngOrJpeg(ReadOnlySpan<byte> b) =>
        b.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) || (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF);
}
