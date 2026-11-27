using System.Diagnostics;
using System.IO;
using Cms.Client;
using Microsoft.Win32;

namespace Cms.Launcher;

/// <summary>
/// "Cms.Launcher.exe --watchdog": the shell of the kiosk user. It has no window; it starts the lock screen and starts it
/// again about 0.5 s after it ends (Cms.Client.RestartPolicy), so a killed lock screen comes back at once (Windows test
/// 2026-11-27, defect 5). One watchdog per Windows session (a named mutex); a second one ends at once. A lock screen that
/// runs already in the session is watched, not started twice. The lock screen starts a new watchdog when this one is
/// gone, and a scheduled task of the kiosk user starts one every minute (see scripts/installer/install-client.ps1).
/// </summary>
internal static class Watchdog
{
    private static readonly TimeSpan EndingWindow = TimeSpan.FromSeconds(60);
    private static long _sessionEndingTicks;

    public static int Run()
    {
        using var mutex = new Mutex(false, LauncherModes.WatchdogMutex);
        if (!SingleInstance.TryAcquire(mutex)) return 0; // another watchdog runs in this session

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return 1;
        using var self = Process.GetCurrentProcess();
        var policy = new RestartPolicy();
        // Windows signs the user out or shuts down: do not start the lock screen again (a cancelled sign-out counts 60 s)
        try { SystemEvents.SessionEnding += (_, _) => Interlocked.Exchange(ref _sessionEndingTicks, DateTimeOffset.UtcNow.UtcTicks); }
        catch (Exception) { /* no system events: the session end kills this process anyway */ }

        while (true)
        {
            Process? lockScreen = null;
            try
            {
                lockScreen = FindRunning(exe, self.Id, self.SessionId) ?? Start(exe);
            }
            catch (Exception)
            {
                // the start failed (for example during an update): tried again after the delay
            }
            if (lockScreen != null)
            {
                using (lockScreen)
                {
                    try { lockScreen.WaitForExit(); } catch (Exception) { /* gone already */ }
                }
            }
            var delay = policy.OnExit(DateTimeOffset.UtcNow);
            if (SessionEnding()) Thread.Sleep(EndingWindow); // a real sign-out ends this process during the wait
            else Thread.Sleep(delay);
        }
    }

    private static bool SessionEnding()
    {
        var ticks = Interlocked.Read(ref _sessionEndingTicks);
        return ticks != 0 && DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero) < EndingWindow;
    }

    /// <summary>The lock screen that runs already in this session (Cms.Client.LockScreenProcesses), or null.</summary>
    private static Process? FindRunning(string exe, int ownPid, int sessionId)
    {
        var all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        try
        {
            var seen = new List<LauncherProcess>();
            foreach (var p in all)
            {
                try { seen.Add(new LauncherProcess(p.Id, p.SessionId, p.MainModule?.FileName, p.StartTime.ToUniversalTime())); }
                catch (Exception) { /* ended, or not ours */ }
            }
            var pid = LockScreenProcesses.PickRunningLockScreen(seen, ownPid, sessionId, exe, DateTimeOffset.UtcNow);
            return pid is { } id ? Process.GetProcessById(id) : null;
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    private static Process? Start(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty });
}
