using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cms.Server.Auth;

public static class DeviceStatuses
{
    /// <summary>Registered without an enrollment token; waits for staff approval. No commands.</summary>
    public const string Pending = "pending";
    public const string Approved = "approved";
    /// <summary>Removed by staff. The key no longer works; the history stays.</summary>
    public const string Revoked = "revoked";
}

/// <summary>
/// Per-device keys. The server stores only "sha256:" + SHA-256 of the key, so a copy of the
/// database does not allow anyone to act as a PC. Keys are 256-bit random values, so an unsalted
/// hash is enough.
/// </summary>
public static class DeviceKeys
{
    public const string HashPrefix = "sha256:";

    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) =>
        HashPrefix + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool IsHashed(string stored) => stored.StartsWith(HashPrefix, StringComparison.Ordinal);

    /// <summary>Databases of earlier versions stored the plain key; replace it with the hash once.</summary>
    public static async Task<int> HashLegacyKeysAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var legacy = await db.Devices.Where(d => !d.DeviceKey.StartsWith(HashPrefix)).ToListAsync(ct);
        foreach (var device in legacy)
            device.DeviceKey = Hash(device.DeviceKey);
        if (legacy.Count > 0) await db.SaveChangesAsync(ct);
        return legacy.Count;
    }
}

/// <summary>
/// How new PCs join the club.
///   CMS_ENROLLMENT_TOKEN or Devices:EnrollmentToken - secret given to the agent installer
///                         (ClubClientSetup.exe -EnrollmentToken ...). A PC with this token is approved at once.
///   Devices:AllowPendingRegistration (default true)  - a PC without the token is added as "pending" and
///                         does nothing until staff approve it in the admin UI (Devices).
///   Devices:MaxPendingDevices (default 50)           - limit for the approval queue.
/// </summary>
public sealed class DeviceEnrollmentOptions
{
    public string? EnrollmentToken { get; init; }
    public bool AllowPendingRegistration { get; init; } = true;
    public int MaxPendingDevices { get; init; } = 50;

    public bool TokenConfigured => !string.IsNullOrEmpty(EnrollmentToken);

    public static DeviceEnrollmentOptions FromConfiguration(IConfiguration config) => new()
    {
        EnrollmentToken = (config["CMS_ENROLLMENT_TOKEN"] ?? config["Devices:EnrollmentToken"])?.Trim() is { Length: > 0 } t ? t : null,
        AllowPendingRegistration = config.GetValue("Devices:AllowPendingRegistration", true),
        MaxPendingDevices = Math.Max(0, config.GetValue("Devices:MaxPendingDevices", 50)),
    };

    public bool TokenMatches(string? candidate)
    {
        if (!TokenConfigured || string.IsNullOrEmpty(candidate)) return false;
        // Compare fixed-length digests in constant time
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(EnrollmentToken!));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Trim()));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

/// <summary>
/// Authentication of agent calls by the X-Device-Key header. Staff cookies are never accepted
/// on device endpoints, and device keys are never accepted on staff endpoints.
/// </summary>
public sealed class DeviceKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DeviceKey";
    public const string HeaderName = "X-Device-Key";
    public const string DeviceIdClaim = "ccms:device_id";
    public const string StatusClaim = "ccms:device_status";

    public DeviceKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key)) return AuthenticateResult.NoResult();

        var hash = DeviceKeys.Hash(key);
        var db = Context.RequestServices.GetRequiredService<CmsDbContext>();
        var device = await db.Devices.AsNoTracking()
            .Where(d => d.DeviceKey == hash)
            .Select(d => new { d.Id, d.Status })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (device == null || device.Status == DeviceStatuses.Revoked)
            return AuthenticateResult.Fail("Unknown device key");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(DeviceIdClaim, device.Id.ToString()),
            new Claim(StatusClaim, device.Status),
        }, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class DevicePrincipal
{
    public static Guid? DeviceId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(DeviceKeyAuthenticationHandler.DeviceIdClaim), out var id) ? id : null;
}
