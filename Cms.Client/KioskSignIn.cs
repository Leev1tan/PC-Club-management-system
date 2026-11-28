using System.Text.Json;

namespace Cms.Client;

/// <summary>The state of a Windows session (WTS_CONNECTSTATE_CLASS of wtsapi32, the same numbers).</summary>
public enum WindowsSessionState
{
    Active = 0, Connected = 1, ConnectQuery = 2, Shadow = 3, Disconnected = 4, Idle = 5, Listen = 6, Reset = 7, Down = 8, Init = 9
}

/// <summary>
/// A Windows session as WTSEnumerateSessions shows it. UserName: "" when the session has no user (session 0, the Windows
/// sign-in screen, a listener); null when the agent could not read it (then it counts as a user: no restart).
/// </summary>
public sealed record WindowsSession(int Id, string? UserName, WindowsSessionState State);

/// <summary>What the agent does about the sign-in of a kiosk PC.</summary>
public enum KioskRestartAction
{
    /// <summary>Nothing now (a user is signed in, not a kiosk PC, or the time has not come).</summary>
    Wait,
    /// <summary>Restart Windows now: the automatic sign-in of ClubUser works only at the start of Windows.</summary>
    RestartNow,
    /// <summary>A restart is due, but the agent restarted Windows 3 times in 60 min already ("kiosk sign-in fails").</summary>
    Blocked
}

public enum KioskRestartReason
{
    /// <summary>Not a kiosk PC (no kiosk.json with the kiosk user): the agent never restarts it.</summary>
    NotKiosk,
    /// <summary>The list of the Windows sessions could not be read: no restart.</summary>
    SessionsUnknown,
    /// <summary>
    /// A user is signed in (ClubUser, ClubAdmin or another account; active or disconnected). Not counted: a disconnected
    /// ClubUser that is the only user session on a locked PC (KioskRestartPolicy.SomebodySignedIn).
    /// </summary>
    UserSignedIn,
    /// <summary>No user, but Windows runs less than 5 min: the automatic sign-in after the boot may still come.</summary>
    WindowsStarting,
    /// <summary>No user since less than 120 s.</summary>
    Counting,
    /// <summary>A restart is due, but the record of the earlier restarts cannot be read: no restart.</summary>
    HistoryUnknown,
    /// <summary>No user for 120 s (and Windows runs 5 min or more).</summary>
    NoUserSignedIn,
    /// <summary>The staff "Log off" of this agent ended the last session.</summary>
    StaffLogoff,
    /// <summary>3 restarts in the last 60 min: the agent does not restart again.</summary>
    GuardFull
}

public sealed record KioskRestartDecision(KioskRestartAction Action, KioskRestartReason Reason);

/// <summary>The inputs of KioskRestartPolicy.Decide (no clock, no I/O: the agent reads them).</summary>
public sealed record KioskRestartInput
{
    /// <summary>kiosk.json next to the lock screen names the kiosk user (KioskAccount.IsKioskInstall).</summary>
    public bool KioskMode { get; init; }
    /// <summary>The kiosk user of kiosk.json (ClubUser).</summary>
    public string? KioskUser { get; init; }
    /// <summary>The lock state of the agent (Cms.Client.AgentLock): false while a player session runs or staff unlocked the PC.</summary>
    public bool Locked { get; init; } = true;
    /// <summary>All Windows sessions; null when the agent could not read them.</summary>
    public IReadOnlyList<WindowsSession>? Sessions { get; init; }
    /// <summary>The time since the start of Windows (GetTickCount64).</summary>
    public TimeSpan Uptime { get; init; }
    /// <summary>How long no user session exists, without a break.</summary>
    public TimeSpan WithoutSessions { get; init; }
    /// <summary>The restarts of the agent (UTC, the state file); null when the file cannot be read.</summary>
    public IReadOnlyList<DateTimeOffset>? RestartHistory { get; init; } = Array.Empty<DateTimeOffset>();
    /// <summary>The staff "Log off" of this agent ended the last user session less than 2 min ago.</summary>
    public bool StaffLogoffPending { get; init; }
    public DateTimeOffset Now { get; init; }
}

/// <summary>
/// When the agent restarts a kiosk PC (Windows re-test 2026-11-28 night, defect 1). Without ForceAutoLogon, Windows 11 does
/// not sign ClubUser in again after a sign-out (staff "Log off", an update, ClubAdmin signs out): the PC stays at the
/// Windows sign-in screen, and only a restart signs ClubUser in (about 1 min after the boot). ForceAutoLogon cannot help: it
/// also signs ClubUser in on "Switch user", so ClubAdmin can never sign in. So on a kiosk PC the agent restarts Windows
/// when no user is signed in for 120 s and Windows runs at least 5 min (a boot before the automatic sign-in never triggers
/// it), and at once when its own staff "Log off" ended the last session. A signed-in user (active or disconnected) stops
/// every restart. At most 3 restarts in 60 min: a sign-in that never works must not restart the PC for ever.
/// One exception (coordinator decision 2026-11-28, "make it work reliably"): a DISCONNECTED session of the kiosk user that
/// is the only user session, on a LOCKED PC, counts as nobody. That is the Windows sign-in screen after "Switch user >
/// ClubAdmin" and a sign-out of ClubAdmin, or after a player's "Switch user" on a locked PC: nobody can sign in again as
/// ClubUser, only a restart helps. While a player session runs (unlocked) it counts as signed in: a restart would end the
/// paid time, so staff decide (their "Log off" restarts at once).
/// </summary>
public static class KioskRestartPolicy
{
    public static readonly TimeSpan NoUserLimit = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MinUptime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan GuardWindow = TimeSpan.FromMinutes(60);
    public const int MaxRestarts = 3;

    /// <summary>
    /// True for a session of a user: not session 0, not a listener and not an ended session, with a user name. A session
    /// whose user cannot be read counts as a user (no restart). The Windows sign-in screen has no user.
    /// </summary>
    public static bool IsUserSession(WindowsSession session) =>
        session.Id != 0 &&
        session.State is not (WindowsSessionState.Listen or WindowsSessionState.Down or WindowsSessionState.Reset) &&
        (session.UserName == null || session.UserName.Trim().Length > 0);

    /// <summary>
    /// True when somebody counts as signed in: a user session exists, except when the only user sessions are disconnected
    /// sessions of the kiosk user and the PC is locked. An unknown session list counts as signed in (fail safe).
    /// </summary>
    public static bool SomebodySignedIn(IReadOnlyList<WindowsSession>? sessions, string? kioskUser, bool locked)
    {
        if (sessions == null) return true;
        var users = sessions.Where(IsUserSession).ToList();
        if (users.Count == 0) return false;
        if (!locked || string.IsNullOrWhiteSpace(kioskUser)) return true;
        return !users.All(u => u.State == WindowsSessionState.Disconnected &&
                               string.Equals(u.UserName?.Trim(), kioskUser.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The restarts of the last 60 min. A time more than 60 min in the future is a clock error and does not count.</summary>
    public static int RecentRestarts(IEnumerable<DateTimeOffset> history, DateTimeOffset now) =>
        history.Count(t => t > now - GuardWindow && t <= now + GuardWindow);

    /// <summary>When the guard allows a restart again (the oldest of the last 3 restarts is 60 min old); null: it allows one now.</summary>
    public static DateTimeOffset? GuardOpensUtc(IEnumerable<DateTimeOffset> history, DateTimeOffset now)
    {
        var recent = history.Where(t => t > now - GuardWindow && t <= now + GuardWindow).OrderBy(t => t).ToList();
        if (recent.Count < MaxRestarts) return null;
        return recent[recent.Count - MaxRestarts] + GuardWindow;
    }

    public static KioskRestartDecision Decide(KioskRestartInput input)
    {
        if (!input.KioskMode) return new(KioskRestartAction.Wait, KioskRestartReason.NotKiosk);
        if (input.Sessions == null) return new(KioskRestartAction.Wait, KioskRestartReason.SessionsUnknown);
        if (SomebodySignedIn(input.Sessions, input.KioskUser, input.Locked)) return new(KioskRestartAction.Wait, KioskRestartReason.UserSignedIn);

        var due = input.StaffLogoffPending || (input.Uptime >= MinUptime && input.WithoutSessions >= NoUserLimit);
        if (!due)
            return new(KioskRestartAction.Wait, input.Uptime < MinUptime ? KioskRestartReason.WindowsStarting : KioskRestartReason.Counting);
        if (input.RestartHistory == null) return new(KioskRestartAction.Wait, KioskRestartReason.HistoryUnknown);
        if (RecentRestarts(input.RestartHistory, input.Now) >= MaxRestarts) return new(KioskRestartAction.Blocked, KioskRestartReason.GuardFull);
        return new(KioskRestartAction.RestartNow, input.StaffLogoffPending ? KioskRestartReason.StaffLogoff : KioskRestartReason.NoUserSignedIn);
    }
}

/// <summary>One check of KioskSignInWatch: the decision, whether it changed (the agent logs it), and the time without a user.</summary>
public sealed record KioskSignInStep(KioskRestartDecision Decision, bool Changed, TimeSpan WithoutSessions);

/// <summary>
/// The agent's view of the sign-in over time: how long no user session exists (without a break), and whether its own staff
/// "Log off" ended the last session. The times are the uptime of Windows (a clock change does not move them). No clock and
/// no I/O, so it is tested on any OS.
/// </summary>
public sealed class KioskSignInWatch
{
    /// <summary>A staff "Log off" counts for the immediate restart for 2 min (the sign-out takes a few seconds).</summary>
    public static readonly TimeSpan StaffLogoffWindow = TimeSpan.FromMinutes(2);

    private TimeSpan? _noUserSince;
    private bool _userSeen;
    private TimeSpan? _logoffAt;
    private bool _logoffEndedSession;
    private KioskRestartDecision? _last;

    /// <summary>
    /// The staff "Log off" arrived. It restarts at once only when a user session existed then (or came later) and is gone:
    /// then the agent knows that it caused the sign-out.
    /// </summary>
    public void StaffLogoff(TimeSpan uptime)
    {
        _logoffAt = uptime;
        _logoffEndedSession = _userSeen;
    }

    /// <summary>The agent asked Windows to restart (or tried): the staff log off is done, and a new 120 s count starts.</summary>
    public void RestartRequested(TimeSpan uptime)
    {
        _noUserSince = uptime;
        _logoffAt = null;
        _logoffEndedSession = false;
    }

    /// <summary>
    /// One check. locked: the lock state of the agent now; it is read on every check, so a session that starts during the
    /// count (unlocked) ends the count, and the count starts again when the PC is locked.
    /// </summary>
    public KioskSignInStep Next(bool kioskMode, string? kioskUser, bool locked, IReadOnlyList<WindowsSession>? sessions, TimeSpan uptime,
        IReadOnlyList<DateTimeOffset>? restartHistory, DateTimeOffset now)
    {
        bool? user = sessions == null ? null : KioskRestartPolicy.SomebodySignedIn(sessions, kioskUser, locked);
        // The 120 s need a known "no user" without a break: an unreadable list starts the count again
        if (!kioskMode || user != false) _noUserSince = null;
        else _noUserSince ??= uptime;

        if (_logoffAt is { } at && uptime - at > StaffLogoffWindow)
        {
            _logoffAt = null;
            _logoffEndedSession = false;
        }
        if (user == true)
        {
            _userSeen = true;
            if (_logoffAt != null) _logoffEndedSession = true;
        }
        else if (user == false)
        {
            _userSeen = false;
        }

        var without = _noUserSince is { } since ? uptime - since : TimeSpan.Zero;
        var decision = KioskRestartPolicy.Decide(new KioskRestartInput
        {
            KioskMode = kioskMode,
            KioskUser = kioskUser,
            Locked = locked,
            Sessions = sessions,
            Uptime = uptime,
            WithoutSessions = without,
            RestartHistory = restartHistory,
            StaffLogoffPending = _logoffAt != null && _logoffEndedSession && user == false,
            Now = now
        });
        var changed = decision != _last;
        _last = decision;
        return new KioskSignInStep(decision, changed, without);
    }
}

/// <summary>
/// The restarts of the agent for the guard (KioskRestartPolicy.MaxRestarts in 60 min), in the file kiosk-restarts.json of
/// the private folder of the agent (only SYSTEM, Administrators and the agent may write it). It survives the restart.
/// </summary>
public static class KioskRestartHistory
{
    public const string FileName = "kiosk-restarts.json";
    public const int MaxEntries = 20;
    public static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    /// <summary>The restart times (oldest first). Empty for no text; null for a text that is not a restart history.</summary>
    public static IReadOnlyList<DateTimeOffset>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DateTimeOffset>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("restartsUtc", out var list)) return Array.Empty<DateTimeOffset>();
            if (list.ValueKind != JsonValueKind.Array) return null;
            var times = new List<DateTimeOffset>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.TryGetDateTimeOffset(out var t)) times.Add(t.ToUniversalTime());
            }
            return times.OrderBy(t => t).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file text with one more restart; entries older than a day go, at most MaxEntries stay.</summary>
    public static string With(IReadOnlyList<DateTimeOffset> history, DateTimeOffset restartUtc)
    {
        var times = history.Where(t => t > restartUtc - Keep).Append(restartUtc).OrderBy(t => t).ToList();
        if (times.Count > MaxEntries) times = times.Skip(times.Count - MaxEntries).ToList();
        return JsonSerializer.Serialize(new { restartsUtc = times.Select(t => t.ToUniversalTime().ToString("O")) },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
