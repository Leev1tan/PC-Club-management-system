using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>
/// Windows re-test 2026-11-27 late, defect 3: on a kiosk PC the lock screen started (in kiosk mode, with the keyboard hook
/// and the close-programs guard) on the desktop of ClubAdmin too. Now it runs only for the kiosk user of kiosk.json.
/// </summary>
public sealed class KioskAccountTests
{
    // The text that install-client.ps1 writes (ConvertTo-Json of Windows PowerShell 5.1)
    private const string InstallerKioskJson = """
        {
            "BlockWinKey":  true,
            "KioskUser":  "ClubUser",
            "KioskMode":  true,
            "InstalledUtc":  "2026-11-27T20:00:00.0000000Z",
            "BlockAltTab":  true,
            "BlockAltF4":  true
        }
        """;

    [Fact]
    public void The_kiosk_user_comes_from_kiosk_json()
    {
        Assert.Equal("ClubUser", KioskAccount.KioskUser(InstallerKioskJson));
        Assert.Equal("ClubUser", KioskAccount.KioskUser("""{"kioskuser":" ClubUser "}"""));
        Assert.Null(KioskAccount.KioskUser("{}"));                       // an older or a test kiosk.json
        Assert.Null(KioskAccount.KioskUser("""{"KioskUser":""}"""));
        Assert.Null(KioskAccount.KioskUser("""{"KioskUser":42}"""));
        Assert.Null(KioskAccount.KioskUser("not json"));
        Assert.Null(KioskAccount.KioskUser("[\"ClubUser\"]"));
        Assert.Null(KioskAccount.KioskUser(null));
    }

    [Theory]
    [InlineData(true, "ClubUser", "ClubUser", true)]
    [InlineData(true, "ClubUser", "clubuser", true)]       // Windows user names are not case-sensitive
    [InlineData(true, "ClubUser", "ClubAdmin", false)]     // the defect: the admin desktop
    [InlineData(true, "ClubUser", "Administrator", false)]
    [InlineData(true, null, "ClubAdmin", true)]            // no name in kiosk.json: fail closed, the lock screen runs
    [InlineData(true, "ClubUser", null, true)]             // the user is not known: fail closed
    [InlineData(false, null, "ClubAdmin", true)]           // no kiosk.json: the lock screen runs for every account
    [InlineData(false, "ClubUser", "ClubAdmin", true)]
    public void The_lock_screen_runs_only_for_the_kiosk_user_on_a_kiosk_pc(bool kiosk, string? kioskUser, string? currentUser, bool runs) =>
        Assert.Equal(runs, KioskAccount.RunsFor(kiosk, kioskUser, currentUser));

    [Fact]
    public void The_program_folder_decides_with_its_kiosk_json()
    {
        var folder = Directory.CreateTempSubdirectory("ccms-kiosk-").FullName;
        try
        {
            Assert.True(KioskAccount.RunsFor(folder, "ClubAdmin"));                             // no kiosk.json
            File.WriteAllText(Path.Combine(folder, KioskAccount.ConfigFile), InstallerKioskJson);
            Assert.True(KioskAccount.RunsFor(folder, "ClubUser"));
            Assert.False(KioskAccount.RunsFor(folder, "ClubAdmin"));
            File.WriteAllText(Path.Combine(folder, KioskAccount.ConfigFile), "{ broken");
            Assert.True(KioskAccount.RunsFor(folder, "ClubAdmin"));                             // unreadable: fail closed
            Assert.True(KioskAccount.RunsFor(Path.Combine(folder, "missing"), "ClubAdmin"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_exit_code_differs_from_the_other_exit_codes() =>
        Assert.NotEqual(LauncherModes.ExitAlreadyRunning, LauncherModes.ExitNotKioskUser);
}
