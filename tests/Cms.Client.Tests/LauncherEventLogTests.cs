using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>Windows test 2026-11-27 night, defect 4: agent.log said "the emergency unlock" for every note of the lock screen.</summary>
public sealed class LauncherEventLogTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 17, 23, 49, TimeSpan.Zero);
    private static readonly Guid Game = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Each_type_of_note_has_its_own_text()
    {
        Assert.Equal("the emergency unlock with the staff PIN at 2026-11-27T17:23:49.0000000+00:00",
            LauncherEventLog.Describe(new LauncherEvent { OccurredUtc = At }));                       // the default type
        Assert.Equal($"the start of game {Game} at 2026-11-27T17:23:49.0000000+00:00",
            LauncherEventLog.Describe(new LauncherEvent { Type = LauncherEvent.GameStarted, GameId = Game, OccurredUtc = At }));
        Assert.StartsWith($"the stop of game {Game}", LauncherEventLog.Describe(new LauncherEvent { Type = LauncherEvent.GameStopped, GameId = Game, OccurredUtc = At }));
        Assert.Equal("the teardown of the ended session at 2026-11-27T17:23:49.0000000+00:00 (2 program(s) closed)",
            LauncherEventLog.Describe(new LauncherEvent { Type = LauncherEvent.SessionTeardown, OccurredUtc = At, Killed = new[] { "msedge", "notepad" } }));
    }

    [Fact]
    public void A_teardown_after_a_staff_unlock_without_a_session_says_so()
    {
        // Windows re-test 2026-11-27 late, LOW 9: no session ended, so the log must not say "the ended session"
        var text = LauncherEventLog.Describe(new LauncherEvent { Type = LauncherEvent.SessionTeardown, OccurredUtc = At, WithoutSession = true });
        Assert.Equal("the teardown at the end of a staff unlock without a session at 2026-11-27T17:23:49.0000000+00:00 (0 program(s) closed)", text);
        Assert.Contains("the ended session",
            LauncherEventLog.Describe(new LauncherEvent { Type = LauncherEvent.SessionTeardown, OccurredUtc = At, WithoutSession = false }));
        Assert.Contains("the ended session",
            LauncherEventLog.Describe(new LauncherEvent { Type = LauncherEvent.SessionTeardown, OccurredUtc = At }));   // an older lock screen
    }

    [Fact]
    public void The_note_of_the_lock_screen_keeps_the_new_field_in_the_inbox_file()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new LauncherEvent { Type = LauncherEvent.SessionTeardown, WithoutSession = true });
        var back = System.Text.Json.JsonSerializer.Deserialize<LauncherEvent>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.True(back!.WithoutSession);
    }

    [Fact]
    public void Only_a_staff_unlock_is_logged_as_an_emergency_unlock()
    {
        foreach (var type in new[] { LauncherEvent.GameStarted, LauncherEvent.GameStopped, LauncherEvent.SessionTeardown, "other" })
            Assert.DoesNotContain("emergency", LauncherEventLog.Describe(new LauncherEvent { Type = type, OccurredUtc = At }));
    }

    [Fact]
    public void An_unknown_type_from_the_inbox_cannot_add_lines_to_the_log()
    {
        var text = LauncherEventLog.Describe(new LauncherEvent { Type = "x\r\n2026 FAKE staff_unlock " + new string('a', 80), OccurredUtc = At });
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("...", text);
        Assert.True(text.Length < 120, text);
    }
}
