using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace Cms.Server.Auth;

public enum PasswordCheck { Failed, Success, SuccessRehashNeeded }

/// <summary>
/// Salted password hashing for staff and players with ASP.NET Core Identity's PasswordHasher
/// (PBKDF2 with HMAC-SHA512, 100 000 iterations, 128-bit random salt, format version 3).
/// </summary>
public static class Passwords
{
    public const int StaffMinLength = 8;
    /// <summary>Players may use a short PIN (typed on the lock screen); login attempts are throttled.</summary>
    public const int PlayerMinLength = 4;
    public const int MaxLength = 128;

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(Subject, Guid.NewGuid().ToString("N")));

    public static string Hash(string password) => Hasher.HashPassword(Subject, password);

    public static PasswordCheck Verify(string? hash, string? password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password) || password.Length > MaxLength)
            return PasswordCheck.Failed;

        // Earlier versions stored players as unsalted SHA-256 (base64, 44 characters). Accept it once;
        // SuccessRehashNeeded makes the caller store a salted hash instead.
        if (TryDecodeLegacySha256(hash, out var legacy))
        {
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return CryptographicOperations.FixedTimeEquals(actual, legacy) ? PasswordCheck.SuccessRehashNeeded : PasswordCheck.Failed;
        }
        try
        {
            return Hasher.VerifyHashedPassword(Subject, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
                _ => PasswordCheck.Failed
            };
        }
        catch (FormatException)
        {
            return PasswordCheck.Failed;
        }
    }

    /// <summary>
    /// Spends the same time as a real check. Used when the account does not exist, so the
    /// response time does not tell an attacker which user names exist.
    /// </summary>
    public static void VerifyDummy(string? password) =>
        Hasher.VerifyHashedPassword(Subject, DummyHash.Value, password ?? string.Empty);

    /// <summary>True for a hash in the old unsalted SHA-256 format.</summary>
    public static bool IsLegacyHash(string? hash) => hash != null && TryDecodeLegacySha256(hash, out _);

    private static bool TryDecodeLegacySha256(string hash, out byte[] digest)
    {
        digest = Array.Empty<byte>();
        if (hash.Length != 44 || !hash.EndsWith('=')) return false; // Identity v3 hashes are 84 characters
        var buffer = new byte[33];
        if (!Convert.TryFromBase64String(hash, buffer, out var written) || written != 32) return false;
        digest = buffer[..32];
        return true;
    }

    /// <summary>Returns an error text, or null when the player password or PIN is acceptable.</summary>
    public static string? ValidatePlayerPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Trim().Length < PlayerMinLength)
            return $"A player password or PIN must have at least {PlayerMinLength} characters.";
        if (password.Length > MaxLength)
            return $"The password must have at most {MaxLength} characters.";
        return null;
    }

    /// <summary>Returns an error text, or null when the staff password is acceptable.</summary>
    public static string? ValidateStaffPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < StaffMinLength)
            return $"The password must have at least {StaffMinLength} characters.";
        if (password.Length > MaxLength)
            return $"The password must have at most {MaxLength} characters.";
        if (string.IsNullOrWhiteSpace(password))
            return "The password must not be only spaces.";
        return null;
    }
}
