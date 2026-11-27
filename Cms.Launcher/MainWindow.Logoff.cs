using System.Runtime.InteropServices;

namespace Cms.Launcher;

/// <summary>
/// "Log off" from the admin UI. The agent is a service in session 0 and cannot end the player's Windows session;
/// the lock screen runs in that session as the player, so it logs it off (ExitWindowsEx, forced: open programs
/// close without asking). The decision (once, fresh, made after this lock screen started) is Cms.Client.LogoffTrigger.
/// </summary>
public partial class MainWindow
{
    private const uint EwxLogoff = 0x00000000;
    private const uint EwxForce = 0x00000004;
    private const uint ShtdnReasonMajorOther = 0x00000000;
    private const uint ShtdnReasonFlagPlanned = 0x80000000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    private void LogOffWindowsSession()
    {
        _loggingOff = true;
        ReportSessionEnd();
        if (!ExitWindowsEx(EwxLogoff | EwxForce, ShtdnReasonMajorOther | ShtdnReasonFlagPlanned))
            _loggingOff = false; // refused: the PC stays as it is (the staff can reboot it)
    }

    /// <summary>
    /// The Windows session ends (log off, shut down, restart): its games end with it. The lock screen reports them as
    /// stopped first, so the admin view does not keep the old current game (Windows test 2026-11-27, defect 9). A forced
    /// log off sends no notice to the programs, so LogOffWindowsSession calls this before it.
    /// </summary>
    private void ReportSessionEnd()
    {
        try { ReportGameEvents(_gameTracker.StopAll(DateTimeOffset.UtcNow)); }
        catch (Exception) { /* best effort: the session ends anyway */ }
    }
}
