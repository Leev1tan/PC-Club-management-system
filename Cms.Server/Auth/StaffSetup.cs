using System.Security.Cryptography;
using System.Text;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Auth;

/// <summary>
/// First start: there is no staff account yet. Two ways to create the first Owner:
///  1. Environment: CMS_INITIAL_OWNER_USERNAME + CMS_INITIAL_OWNER_PASSWORD
///     (or Setup:OwnerUsername / Setup:OwnerPassword). Used only while no staff account exists.
///  2. Setup screen in the admin UI. It needs the one-time setup code that the server writes to
///     &lt;data directory&gt;/setup-code.txt, so only someone with access to the server can use it.
///     The screen and the code stop working as soon as a staff account exists.
/// Demo mode seeds its own demo staff accounts instead.
/// </summary>
public sealed class SetupState
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I
    private readonly object _gate = new();
    private string? _code;

    public string CodeFilePath { get; }

    public SetupState(ServerPaths paths)
    {
        CodeFilePath = paths.Combine("setup-code.txt");
    }

    /// <summary>True while the setup screen accepts a setup code.</summary>
    public bool IsOpen { get { lock (_gate) return _code != null; } }

    /// <summary>Creates a new random setup code (12 characters, about 60 bits) and returns it.</summary>
    public string Open()
    {
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
        lock (_gate) _code = code;
        return code;
    }

    public bool Check(string? candidate)
    {
        string? code;
        lock (_gate) code = _code;
        if (code == null || string.IsNullOrWhiteSpace(candidate)) return false;
        var a = Encoding.UTF8.GetBytes(Canonical(code));
        var b = Encoding.UTF8.GetBytes(Canonical(candidate));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void Close()
    {
        lock (_gate) _code = null;
        try { File.Delete(CodeFilePath); } catch { /* best effort */ }
    }

    private static string Canonical(string code) => code.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
}

public static class StaffBootstrapper
{
    public const string OwnerUsernameVariable = "CMS_INITIAL_OWNER_USERNAME";
    public const string OwnerPasswordVariable = "CMS_INITIAL_OWNER_PASSWORD";

    /// <summary>Runs once at startup, after the database schema is ready.</summary>
    public static async Task RunAsync(IServiceProvider services, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var setup = scope.ServiceProvider.GetRequiredService<SetupState>();

        var username = config[OwnerUsernameVariable] ?? config["Setup:OwnerUsername"];
        var password = config[OwnerPasswordVariable] ?? config["Setup:OwnerPassword"];
        var fromEnvironment = !string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(password);

        if (await db.StaffUsers.AnyAsync(ct))
        {
            setup.Close();
            if (fromEnvironment)
                logger.LogWarning("{Variable} is set, but staff accounts already exist, so it is ignored. Remove it from the server environment.",
                    OwnerPasswordVariable);
            return;
        }

        if (fromEnvironment)
        {
            var name = StaffNames.Normalize(username);
            var error = !StaffNames.IsValid(name)
                ? $"{OwnerUsernameVariable} must be 2-64 characters: a-z, 0-9, '.', '_' or '-'."
                : Passwords.ValidateStaffPassword(password);
            if (error == null)
            {
                await CreateOwnerAsync(db, name, name, password!, ct);
                setup.Close();
                logger.LogWarning("Created the initial owner account '{Username}' from the environment. " +
                                  "Remove {Variable} from the server environment now.", name, OwnerPasswordVariable);
                return;
            }
            logger.LogError("Cannot create the initial owner from the environment: {Error}", error);
        }

        var code = setup.Open();
        try
        {
            await File.WriteAllTextAsync(setup.CodeFilePath, code + Environment.NewLine, ct);
            logger.LogWarning("No staff account exists yet. Open the admin UI and create the owner account " +
                              "with the one-time setup code from {Path}", setup.CodeFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("No staff account exists yet, and {Path} cannot be written ({Message}). " +
                              "One-time setup code for the admin UI: {Code}", setup.CodeFilePath, ex.Message, code);
        }
    }

    /// <summary>
    /// Creates the first Owner. The setting row "setup.completed" has a primary key, so two
    /// concurrent setup requests cannot both succeed.
    /// </summary>
    public static async Task<StaffUserEntity> CreateOwnerAsync(CmsDbContext db, string username, string displayName, string password, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new StaffUserEntity
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
            PasswordHash = Passwords.Hash(password),
            Role = StaffRoles.Owner,
            IsActive = true,
            CreatedUtc = now
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ClubSettings.Add(new ClubSettingEntity { Key = ClubSettingKeys.SetupCompleted, Value = now.ToString("O"), UpdatedUtc = now, UpdatedBy = username });
        db.StaffUsers.Add(owner);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return owner;
    }
}
