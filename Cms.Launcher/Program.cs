using Cms.Client;

namespace Cms.Launcher;

/// <summary>
/// Entry point. "Cms.Launcher.exe --watchdog" is the supervisor of the kiosk session (no window, see Watchdog); without
/// arguments it is the lock screen, one per Windows session (a second one ends at once with exit code 3).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (LauncherModes.IsWatchdog(args)) return Watchdog.Run();

        using var single = new Mutex(false, LauncherModes.LockScreenMutex);
        if (!SingleInstance.TryAcquire(single)) return LauncherModes.ExitAlreadyRunning;
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        finally
        {
            try { single.ReleaseMutex(); } catch (ApplicationException) { /* not owned any more */ }
        }
    }
}

internal static class SingleInstance
{
    /// <summary>True when this process now owns the mutex (also when its last owner was killed: an abandoned mutex).</summary>
    public static bool TryAcquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }
}
