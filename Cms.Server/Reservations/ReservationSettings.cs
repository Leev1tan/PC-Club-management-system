using System.Text.Json;
using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Reservations;

/// <summary>
/// Club settings of the reservations (Owner, Manager; ClubSettings key reservations.settings, JSON):
///   HoldMinutes (15)          - from this many minutes before the start the PC is held for the reservation
///   GraceMinutes (15)         - a confirmed reservation without check-in is a no-show this many minutes after the start
///   NoShowKeepsDeposit (true) - a no-show keeps the deposit (revenue); false = it goes back to the balance
/// </summary>
public sealed record ReservationSettings
{
    public int HoldMinutes { get; init; } = 15;
    public int GraceMinutes { get; init; } = 15;
    public bool NoShowKeepsDeposit { get; init; } = true;

    public static readonly ReservationSettings Default = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? Validate()
    {
        if (HoldMinutes is < 0 or > 180) return "The hold must be 0 to 180 minutes.";
        if (GraceMinutes is < 5 or > 120) return "The grace period must be 5 to 120 minutes.";
        return null;
    }

    public static async Task<ReservationSettings> LoadAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var json = await db.ClubSettings.AsNoTracking().Where(s => s.Key == ClubSettingKeys.Reservations).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var settings = JsonSerializer.Deserialize<ReservationSettings>(json, Json) ?? Default;
            return settings.Validate() == null ? settings : Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>Stores the settings (the caller audits). Returns the settings that were stored before.</summary>
    public static async Task<ReservationSettings> SaveAsync(CmsDbContext db, ReservationSettings settings, string? by, DateTimeOffset now, CancellationToken ct = default)
    {
        var before = await LoadAsync(db, ct);
        var row = await db.ClubSettings.FirstOrDefaultAsync(s => s.Key == ClubSettingKeys.Reservations, ct);
        if (row == null)
        {
            row = new ClubSettingEntity { Key = ClubSettingKeys.Reservations };
            db.ClubSettings.Add(row);
        }
        row.Value = JsonSerializer.Serialize(settings, Json);
        row.UpdatedUtc = now;
        row.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        return before;
    }
}
