using System.Text.RegularExpressions;

namespace Cms.Server.Auth;

/// <summary>
/// Staff roles (spec area 6):
///   Owner   - everything, including staff accounts and club settings (kiosk PIN)
///   Manager - day-to-day management: tariffs, zones, deletes, balance corrections, audit log
///   Cashier - front desk: sessions, players, top-ups, PC commands; no tariff changes, no deletes,
///             no corrections of past data
///   Tech    - PCs only: device list, commands, zones of PCs, device approval
/// </summary>
public static class StaffRoles
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Cashier = "Cashier";
    public const string Tech = "Tech";

    public static readonly string[] All = { Owner, Manager, Cashier, Tech };

    /// <summary>Returns the canonical role name, or null for an unknown role.</summary>
    public static string? Normalize(string? role) =>
        All.FirstOrDefault(r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Authorization policies used on the API endpoints.</summary>
public static class Policies
{
    /// <summary>Any signed-in staff member (also the fallback for endpoints without an attribute).</summary>
    public const string AnyStaff = "AnyStaff";
    /// <summary>Owner, Manager, Cashier: sessions, players, top-ups.</summary>
    public const string FrontDesk = "FrontDesk";
    /// <summary>Owner, Manager, Tech: device approval, zone and position of PCs.</summary>
    public const string DeviceAdmin = "DeviceAdmin";
    /// <summary>Owner, Manager: tariffs, zones, deletes, balance corrections, revenue, audit log.</summary>
    public const string Management = "Management";
    /// <summary>Owner only: staff accounts and club settings.</summary>
    public const string OwnerOnly = "OwnerOnly";

    /// <summary>Agent of a registered PC (X-Device-Key), also while it waits for approval.</summary>
    public const string Device = "Device";
    /// <summary>Agent of an approved PC: may fetch and acknowledge commands.</summary>
    public const string ApprovedDevice = "ApprovedDevice";

    internal static readonly IReadOnlyDictionary<string, string[]> Roles = new Dictionary<string, string[]>
    {
        [AnyStaff] = StaffRoles.All,
        [FrontDesk] = new[] { StaffRoles.Owner, StaffRoles.Manager, StaffRoles.Cashier },
        [DeviceAdmin] = new[] { StaffRoles.Owner, StaffRoles.Manager, StaffRoles.Tech },
        [Management] = new[] { StaffRoles.Owner, StaffRoles.Manager },
        [OwnerOnly] = new[] { StaffRoles.Owner },
    };
}

public static partial class StaffNames
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex UsernamePattern();

    public static string Normalize(string? username) => (username ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>2-64 characters: lower-case letters, digits, '.', '_' and '-'.</summary>
    public static bool IsValid(string username) => UsernamePattern().IsMatch(username);
}
