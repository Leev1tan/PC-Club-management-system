using System.Text.Json;

namespace Cms.Client;

/// <summary>
/// For which Windows account the lock screen (and its watchdog) runs (Windows re-test 2026-11-27 late, defect 3). A kiosk
/// install (kiosk.json next to Cms.Launcher.exe) runs it only for the kiosk account named in kiosk.json ("KioskUser",
/// ClubUser): the shell of ClubUser starts it. An administrator who signs in on the PC (ClubAdmin, through "Switch user")
/// gets the normal desktop: before, the common Startup shortcut started the lock screen in kiosk mode for ClubAdmin too,
/// with the keyboard hook and the close-programs guard. Without kiosk.json the lock screen runs for every account (it
/// starts from the common Startup folder). Fail closed: an unreadable kiosk.json, or one without a name, keeps the lock
/// screen. Only administrators can change kiosk.json (C:\ClubLauncher: SYSTEM and Administrators only).
/// </summary>
public static class KioskAccount
{
    public const string ConfigFile = "kiosk.json";

    /// <summary>The kiosk account of kiosk.json ("KioskUser"), or null when the text has none or is not JSON.</summary>
    public static string? KioskUser(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "KioskUser", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    return string.IsNullOrWhiteSpace(property.Value.GetString()) ? null : property.Value.GetString()!.Trim();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the lock screen runs for this Windows user. kiosk: kiosk.json exists. False only when kiosk.json names
    /// another account than the user of this process.
    /// </summary>
    public static bool RunsFor(bool kiosk, string? kioskUser, string? currentUser) =>
        !kiosk || string.IsNullOrWhiteSpace(kioskUser) || string.IsNullOrWhiteSpace(currentUser) ||
        string.Equals(kioskUser.Trim(), currentUser.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>RunsFor with the kiosk.json in the program folder. An error while it is read keeps the lock screen.</summary>
    public static bool RunsFor(string programFolder, string? currentUser)
    {
        try
        {
            var path = Path.Combine(programFolder, ConfigFile);
            if (!File.Exists(path)) return true;
            return RunsFor(true, KioskUser(File.ReadAllText(path)), currentUser);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
