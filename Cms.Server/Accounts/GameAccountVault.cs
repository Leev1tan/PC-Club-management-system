using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Cms.Server.Accounts;

/// <summary>
/// Encryption at rest of the club accounts (spec area 8): ASP.NET Core Data Protection with a purpose of its own, so a
/// payload of the login cookies can never be read as an account secret and the other way round. The keys are those of
/// the server (data directory "keys", the same as the login cookies): back them up with the database; without them the
/// stored secrets cannot be read and must be rotated. Plain values exist only in memory, never in logs or exceptions.
/// </summary>
public sealed class GameAccountVault
{
    public const string Purpose = "Cms.GameAccounts.Credentials.v1";
    private readonly IDataProtector _protector;

    public GameAccountVault(IDataProtectionProvider provider) => _protector = provider.CreateProtector(Purpose);

    /// <summary>A vault with keys only in memory (tests, the demo without a server).</summary>
    public static GameAccountVault Ephemeral() => new(new EphemeralDataProtectionProvider());

    public string Protect(string plain) => _protector.Protect(plain);

    /// <summary>The plain value, or null when the keys cannot read it (lost or changed keys, a damaged value).</summary>
    public string? TryUnprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            return _protector.Unprotect(cipher);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>A random secret of fake accounts (demo): 24 characters, letters and digits.</summary>
    public static string RandomSecret()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(24, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }
}
