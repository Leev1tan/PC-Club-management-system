using Cms.Shared;

namespace Cms.Client;

/// <summary>
/// The agent log line for a note of the lock screen that the agent sent to the server (Windows test 2026-11-27 night,
/// defect 4: every note was logged as "the emergency unlock", so staff who read agent.log saw false emergency unlocks).
/// The type comes from a file in the inbox, where players may create files: an unknown type is shortened and cleaned, so
/// it cannot add lines to the log.
/// </summary>
public static class LauncherEventLog
{
    public const int MaxTypeLength = 40;

    /// <summary>What the note was, for "Reported {0} to the server".</summary>
    public static string Describe(LauncherEvent e)
    {
        var at = e.OccurredUtc.ToString("O");
        return e.Type switch
        {
            LauncherEvent.StaffUnlock => $"the emergency unlock with the staff PIN at {at}",
            LauncherEvent.GameStarted => $"the start of game {e.GameId?.ToString() ?? "?"} at {at}",
            LauncherEvent.GameStopped => $"the stop of game {e.GameId?.ToString() ?? "?"} at {at}",
            LauncherEvent.SessionTeardown when e.WithoutSession == true =>
                $"the teardown at the end of a staff unlock without a session at {at} ({e.Killed?.Length ?? 0} program(s) closed)",
            LauncherEvent.SessionTeardown => $"the teardown of the ended session at {at} ({e.Killed?.Length ?? 0} program(s) closed)",
            _ => $"the lock screen note \"{Clean(e.Type)}\" at {at}"
        };
    }

    private static string Clean(string? type)
    {
        if (string.IsNullOrEmpty(type)) return "?";
        var chars = type.Take(MaxTypeLength).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '?').ToArray();
        return new string(chars) + (type.Length > MaxTypeLength ? "..." : string.Empty);
    }
}
