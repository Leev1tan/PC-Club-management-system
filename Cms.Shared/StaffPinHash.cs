using System.Security.Cryptography;
using System.Text;

namespace Cms.Shared;

/// <summary>
/// Hash of the staff / emergency PIN of the lock screen. The server creates it when the owner sets
/// the PIN, the agent stores it on each club PC, and Cms.Launcher verifies a typed PIN locally, so the
/// emergency unlock also works while the server or the agent is down.
/// Format: pbkdf2-sha256$&lt;iterations&gt;$&lt;salt base64&gt;$&lt;hash base64&gt; (PBKDF2-HMAC-SHA256, 128-bit salt).
/// The hash is readable on the club PCs, so the PIN must be long enough to resist guessing offline.
/// </summary>
public static class StaffPinHash
{
    public const string Prefix = "pbkdf2-sha256";
    public const int DefaultIterations = 600_000; // OWASP 2023 recommendation for PBKDF2-HMAC-SHA256
    public const int MinLength = 8;
    public const int MaxLength = 64;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    /// <summary>Returns an error text, or null when the PIN is acceptable.</summary>
    public static string? Validate(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < MinLength)
            return $"The staff PIN must have at least {MinLength} characters (digits and letters are best).";
        if (pin.Length > MaxLength)
            return $"The staff PIN must have at most {MaxLength} characters.";
        if (pin.Distinct().Count() < 3)
            return "The staff PIN is too simple.";
        return null;
    }

    public static string Create(string pin, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool IsWellFormed(string? encoded) => TryParse(encoded, out _, out _, out _);

    public static bool Verify(string? pin, string? encoded)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length > MaxLength) return false;
        if (!TryParse(encoded, out var iterations, out var salt, out var expected)) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryParse(string? encoded, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = Array.Empty<byte>();
        var parts = encoded?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out iterations)
            || iterations < MinIterations || iterations > MaxIterations) return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length >= SaltBytes && hash.Length >= 16;
    }
}

/// <summary>Files that the agent writes for the launcher in %ProgramData%\ClubAgent.</summary>
public static class AgentFiles
{
    public const string DirectoryName = "ClubAgent";
    /// <summary>Only SYSTEM, Administrators and the agent service account: device key, enrollment token.</summary>
    public const string PrivateDirectoryName = "private";
    public const string StateFile = "state.json";
    public const string HeartbeatFile = "agent_heartbeat.txt";
    public const string ConfigFile = "config.json";
    public const string StaffPinFile = "staff-pin.json";
    public const string DeviceFile = "device.json";
    public const string EnrollmentTokenFile = "enrollment-token.txt";
    /// <summary>
    /// Drop box of the launcher (it runs as the player): players may create files here, but cannot
    /// read, change or delete them; the agent reads them and sends them to the server (audit log).
    /// </summary>
    public const string InboxDirectoryName = "inbox";

    /// <summary>The launcher locks the PC when the agent heartbeat file is older than this.</summary>
    public static readonly TimeSpan AgentSilentAfter = TimeSpan.FromSeconds(15);

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DirectoryName);
}

/// <summary>Content of staff-pin.json.</summary>
public sealed class StaffPinFile
{
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Event that the lock screen drops into the agent inbox (AgentFiles.InboxDirectoryName).</summary>
public sealed class LauncherEvent
{
    public const string StaffUnlock = "staff_unlock";
    public const int MaxFileBytes = 4096;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = StaffUnlock;
    public DateTimeOffset OccurredUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Why the PC was locked: LockedByAgent, AgentSilent, NoAgentState (Cms.Shared.LockReason).</summary>
    public string? LockReason { get; set; }
    /// <summary>Windows account that ran the lock screen.</summary>
    public string? WindowsUser { get; set; }

    public static string FileName(Guid id) => $"event-{id:N}.json";
}
