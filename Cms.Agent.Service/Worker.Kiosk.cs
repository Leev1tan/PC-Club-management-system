using System.ComponentModel;
using System.Runtime.InteropServices;
using Cms.Client;

namespace Cms.Agent.Service;

/// <summary>
/// The sign-in of ClubUser on a kiosk PC (Windows re-test 2026-11-28 night, defect 1). Without ForceAutoLogon, Windows 11
/// signs ClubUser in automatically only at the start of Windows, not after a sign-out (staff "Log off", an update,
/// ClubAdmin signs out): the PC stayed at the Windows sign-in screen. So on a kiosk PC the agent restarts Windows when no
/// user is signed in (Cms.Client.KioskRestartPolicy: 120 s without a user and Windows runs 5 min; at once after its own
/// staff "Log off"; at most 3 restarts in 60 min). This file has only the Windows calls: the session list (wtsapi32), the
/// uptime (GetTickCount64), the state file of the guard, and the restart (SeShutdownPrivilege, InitiateSystemShutdownExW).
/// </summary>
public partial class Worker
{
    private static readonly TimeSpan KioskCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly string KioskRestartFile = Path.Combine(PrivateDir, KioskRestartHistory.FileName);

    private readonly KioskSignInWatch _kioskWatch = new();
    private readonly object _kioskGate = new();
    /// <summary>The guard blocks a restart ("kiosk sign-in fails"): the heartbeat reports LockScreenStates.KioskSignInFailed.</summary>
    private volatile bool _kioskSignInFailed;
    /// <summary>Windows accepted the restart: the checks stop (the service stops with Windows).</summary>
    private volatile bool _kioskRestartRequested;
    private bool _kioskHistoryWarned;
    /// <summary>The Win32 error of the last failed WTSEnumerateSessions (for the log line).</summary>
    private int _kioskSessionError;

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateSessionsW")]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private const int WtsUserName = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "InitiateSystemShutdownExW")]
    private static extern bool InitiateSystemShutdownEx(string? machineName, string? message, int timeout, bool forceAppsClosed, bool rebootAfterShutdown, uint reason);

    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const int SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    // SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_MAINTENANCE | SHTDN_REASON_FLAG_PLANNED
    private const uint KioskRestartReasonCode = 0x00040000 | 0x00000001 | 0x80000000;

    private async Task RunKioskSignInLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(KioskCheckInterval);
        try
        {
            do
            {
                if (!_kioskRestartRequested) CheckKioskSignIn();
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // service stops
        }
    }

    private static TimeSpan WindowsUptime() => TimeSpan.FromMilliseconds(GetTickCount64());

    /// <summary>
    /// The kiosk user of kiosk.json next to the lock screen of this install, or null when this is not a kiosk PC. An
    /// unreadable file: not a kiosk PC (no restart).
    /// </summary>
    private string? KioskUserOfThisPc()
    {
        try
        {
            var path = KioskAccount.ConfigPathFor(_config["AccountPool:LauncherPath"] ?? DefaultLauncherPath);
            return File.Exists(path) ? KioskAccount.KioskInstallUser(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "kiosk.json cannot be read: no kiosk restarts");
            return null;
        }
    }

    /// <summary>The lock state of the agent now (AgentLock): false while a player session runs or staff unlocked the PC.</summary>
    private bool AgentLocked()
    {
        lock (_gate) return _lock.Evaluate(DateTimeOffset.UtcNow).Locked;
    }

    /// <summary>All Windows sessions, or null when the list cannot be read (then the agent never restarts).</summary>
    private List<WindowsSession>? ReadWindowsSessions()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            _kioskSessionError = Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            var sessions = new List<WindowsSession>(count);
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(list + i * size);
                var state = Enum.IsDefined(typeof(WindowsSessionState), info.State) ? (WindowsSessionState)info.State : WindowsSessionState.Active;
                sessions.Add(new WindowsSession(info.SessionId, ReadSessionUser(info.SessionId), state));
            }
            return sessions;
        }
        finally
        {
            WTSFreeMemory(list);
        }
    }

    /// <summary>The user name of a session; "" when it has none; null when it cannot be read (counts as a user).</summary>
    private static string? ReadSessionUser(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsUserName, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>The restarts of the guard, or null when the file cannot be read (no restart then). A broken file counts as empty.</summary>
    private IReadOnlyList<DateTimeOffset>? ReadKioskRestarts()
    {
        try
        {
            var text = File.Exists(KioskRestartFile) ? File.ReadAllText(KioskRestartFile) : null;
            var history = KioskRestartHistory.Parse(text);
            if (history != null) return history;
            if (!_kioskHistoryWarned) _logger.LogWarning("{Path} is not a restart record; the agent writes a new one at the next restart", KioskRestartFile);
            _kioskHistoryWarned = true;
            return Array.Empty<DateTimeOffset>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_kioskHistoryWarned) _logger.LogWarning("Cannot read {Path} ({Error}): the agent does not restart this kiosk PC", KioskRestartFile, ex.Message);
            _kioskHistoryWarned = true;
            return null;
        }
    }

    private void CheckKioskSignIn()
    {
        var kioskUser = KioskUserOfThisPc();
        var kiosk = kioskUser != null;
        var sessions = kiosk ? ReadWindowsSessions() : null;
        // Read on every check: a session that starts during the count (unlocked) ends the count
        var locked = AgentLocked();
        var noUser = sessions != null && !KioskRestartPolicy.SomebodySignedIn(sessions, kioskUser, locked);
        var history = kiosk && noUser ? ReadKioskRestarts() : Array.Empty<DateTimeOffset>();
        var uptime = WindowsUptime();
        var now = DateTimeOffset.UtcNow;
        KioskSignInStep step;
        lock (_kioskGate) step = _kioskWatch.Next(kiosk, kioskUser, locked, sessions, uptime, history, now);
        _kioskSignInFailed = step.Decision.Action == KioskRestartAction.Blocked;
        if (step.Changed) LogKioskDecision(step, sessions, history, now);
        if (step.Decision.Action == KioskRestartAction.RestartNow)
            RestartForKioskSignIn(step.Decision, kioskUser, history ?? Array.Empty<DateTimeOffset>(), uptime, now);
    }

    /// <summary>One log line for each change of the decision (the count starts, a user signed in, a restart, the guard).</summary>
    private void LogKioskDecision(KioskSignInStep step, IReadOnlyList<WindowsSession>? sessions, IReadOnlyList<DateTimeOffset>? history, DateTimeOffset now)
    {
        string Users() => sessions == null ? "?" : string.Join(", ", sessions.Where(KioskRestartPolicy.IsUserSession)
            .Select(s => $"{s.UserName ?? "?"} (session {s.Id}, {s.State})"));
        switch (step.Decision.Reason)
        {
            case KioskRestartReason.NotKiosk:
                _logger.LogDebug("Not a kiosk PC: the agent never restarts it for the sign-in");
                break;
            case KioskRestartReason.SessionsUnknown:
                _logger.LogWarning("Kiosk PC: the agent cannot read the Windows sessions (error {Error}); it does not restart the PC",
                    _kioskSessionError);
                break;
            case KioskRestartReason.UserSignedIn:
                _logger.LogInformation("Kiosk PC: signed in: {Users} ({Lock}). The agent does not restart the PC", Users(),
                    AgentLocked() ? "the PC is locked" : "the PC is unlocked: a player session may run");
                break;
            case KioskRestartReason.WindowsStarting:
                _logger.LogInformation("Kiosk PC: nobody is signed in yet; Windows started {Minutes:0.0} min ago. If nobody signs in, the agent " +
                                       "restarts Windows when Windows runs {Min} min and nobody was signed in for {Limit} s",
                    WindowsUptime().TotalMinutes, KioskRestartPolicy.MinUptime.TotalMinutes, KioskRestartPolicy.NoUserLimit.TotalSeconds);
                break;
            case KioskRestartReason.Counting:
                if (sessions != null && sessions.Any(KioskRestartPolicy.IsUserSession))
                    _logger.LogInformation("Kiosk PC: only the disconnected session of the kiosk user is left ({Users}; Switch user) and the PC " +
                                           "is locked: the Windows sign-in screen. The agent restarts Windows in {Limit} s if nobody signs in; " +
                                           "then ClubUser signs in automatically", Users(), KioskRestartPolicy.NoUserLimit.TotalSeconds);
                else
                    _logger.LogInformation("Kiosk PC: nobody is signed in (the Windows sign-in screen). The agent restarts Windows in {Limit} s " +
                                           "if nobody signs in; then ClubUser signs in automatically", KioskRestartPolicy.NoUserLimit.TotalSeconds);
                break;
            case KioskRestartReason.HistoryUnknown:
                _logger.LogWarning("Kiosk PC: a restart is due, but the record of the earlier restarts cannot be read: no restart");
                break;
            case KioskRestartReason.GuardFull:
                var opens = history == null ? null : KioskRestartPolicy.GuardOpensUtc(history, now);
                _logger.LogError("Kiosk sign-in fails: nobody is signed in on this kiosk PC, and the agent restarted Windows {Count} times in " +
                                 "{Window} min already. It does not restart Windows again before {Opens:O}. Check the automatic sign-in of " +
                                 "ClubUser (Winlogon AutoAdminLogon and DefaultUserName, the task ClubKioskAutoLogon) or sign in as ClubAdmin. " +
                                 "To allow restarts at once, delete {Path}",
                    KioskRestartPolicy.MaxRestarts, KioskRestartPolicy.GuardWindow.TotalMinutes, opens ?? now, KioskRestartFile);
                break;
            // RestartNow: RestartForKioskSignIn writes the line
        }
    }

    /// <summary>
    /// Restarts Windows for the sign-in of ClubUser. The restart goes into the record of the guard first: without the record
    /// there is no restart (else a sign-in that never works could restart the PC for ever).
    /// </summary>
    private void RestartForKioskSignIn(KioskRestartDecision decision, string? kioskUser, IReadOnlyList<DateTimeOffset> history, TimeSpan uptime, DateTimeOffset now)
    {
        // The last look: somebody may have signed in, or a session may have started (unlocked), during this check
        var sessions = ReadWindowsSessions();
        if (KioskRestartPolicy.SomebodySignedIn(sessions, kioskUser, AgentLocked())) return;

        lock (_kioskGate) _kioskWatch.RestartRequested(uptime);
        try
        {
            WriteAtomic(KioskRestartFile, KioskRestartHistory.With(history, now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Kiosk PC: cannot write {Path} ({Error}): no restart (the limit of {Max} restarts in {Window} min needs the record)",
                KioskRestartFile, ex.Message, KioskRestartPolicy.MaxRestarts, KioskRestartPolicy.GuardWindow.TotalMinutes);
            return;
        }

        var number = KioskRestartPolicy.RecentRestarts(history, now) + 1;
        if (decision.Reason == KioskRestartReason.StaffLogoff)
            _logger.LogWarning("Kiosk PC: restarting Windows now: the staff Log off ended the session, and Windows signs ClubUser in only " +
                               "at the start (restart {Number} of at most {Max} in {Window} min)",
                number, KioskRestartPolicy.MaxRestarts, KioskRestartPolicy.GuardWindow.TotalMinutes);
        else
            _logger.LogWarning("Kiosk PC: restarting Windows now: nobody was signed in for {Limit} s, and Windows signs ClubUser in only " +
                               "at the start (restart {Number} of at most {Max} in {Window} min)",
                KioskRestartPolicy.NoUserLimit.TotalSeconds, number, KioskRestartPolicy.MaxRestarts, KioskRestartPolicy.GuardWindow.TotalMinutes);

        var error = RequestWindowsRestart();
        if (error == 0)
        {
            _kioskRestartRequested = true;
            return;
        }
        _logger.LogError("Kiosk PC: Windows refused the restart: {Error}. The agent tries again after {Limit} s without a user",
            error == ErrorNotAllAssigned
                ? "the agent service has no SeShutdownPrivilege (sc.exe qprivs ClubAgent)"
                : $"error {error} ({new Win32Exception(error).Message})",
            KioskRestartPolicy.NoUserLimit.TotalSeconds);
    }

    /// <summary>
    /// Restarts Windows at once (no user is signed in, so no warning time). LocalService has SeShutdownPrivilege, disabled:
    /// it is enabled in the token of the agent first. 0 = Windows accepted the restart; else the Win32 error.
    /// </summary>
    private static int RequestWindowsRestart()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return Marshal.GetLastWin32Error();
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return Marshal.GetLastWin32Error();
            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return Marshal.GetLastWin32Error();
            // AdjustTokenPrivileges returns true also when the token has no such privilege: ERROR_NOT_ALL_ASSIGNED
            var adjust = Marshal.GetLastWin32Error();
            if (adjust == ErrorNotAllAssigned) return adjust;
        }
        finally
        {
            CloseHandle(token);
        }
        return InitiateSystemShutdownEx(null, null, 0, true, true, KioskRestartReasonCode) ? 0 : Marshal.GetLastWin32Error();
    }

    /// <summary>The staff "Log off" on a kiosk PC: after the session is gone the agent restarts at once (KioskSignInWatch).</summary>
    private bool NoteKioskStaffLogoff()
    {
        if (KioskUserOfThisPc() == null) return false;
        lock (_kioskGate) _kioskWatch.StaffLogoff(WindowsUptime());
        return true;
    }
}
