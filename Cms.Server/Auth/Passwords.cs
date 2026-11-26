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
    public const int MaxLength = 128;

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Subject = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(Subject, Guid.NewGuid().ToString("N")));

    public static string Hash(string password) => Hasher.HashPassword(Subject, password);

    public static PasswordCheck Verify(string? hash, string? password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password) || password.Length > MaxLength)
            return PasswordCheck.Failed;
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
