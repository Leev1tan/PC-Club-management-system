using System.Diagnostics;
using System.IO;
using Cms.Client;
using Cms.Shared;

namespace Cms.Launcher;

/// <summary>
/// The lock screen and its watchdog watch each other (Cms.Client.LauncherModes): in kiosk mode the lock screen starts a
/// new watchdog when the watchdog of the session is gone. Every 4 s the lock screen reports its state to the agent
/// ("status" on the pipe), so the agent can log a lock screen that is gone while the PC must be locked.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);
    private DateTimeOffset _watchdogStartedUtc = DateTimeOffset.MinValue;
    private bool _statusBusy;
    private int _statusTicks;

    /// <summary>Kiosk mode: starts "Cms.Launcher.exe --watchdog" when no watchdog runs in this session (at most every 5 s).</summary>
    private void EnsureWatchdog()
    {
        if (!_kioskMode) return;
        try
        {
            if (Mutex.TryOpenExisting(LauncherModes.WatchdogMutex, out var existing))
            {
                existing.Dispose(); // the handle must not keep the name alive
                return;
            }
            if (DateTimeOffset.UtcNow - _watchdogStartedUtc < TimeSpan.FromSeconds(5)) return;
            _watchdogStartedUtc = DateTimeOffset.UtcNow;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var watchdog = Process.Start(new ProcessStartInfo(exe, LauncherModes.WatchdogArg)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
            });
        }
        catch (Exception)
        {
            // tried again on the next tick
        }
    }

    /// <summary>
    /// The state of this lock screen for the agent (best effort, every second tick of the 2 s timer), with the lock generation
    /// of the state.json it comes from: the agent ignores a report from before its newest lock.
    /// </summary>
    private async void ReportStatus(LockDecision decision, long? lockGeneration)
    {
        if (_statusBusy || ++_statusTicks % 2 != 0) return;
        _statusBusy = true;
        try
        {
            await AgentPipeClient.SendAsync(AgentPipe.Name,
                new PipeRequest(PipeOps.Status, Locked: decision.Locked, StaffUnlock: decision.StaffOverride, LockGeneration: lockGeneration),
                StatusTimeout, VerifyAgentPipe);
        }
        catch (Exception)
        {
            // the agent does not answer: the lock screen shows that itself (fail closed)
        }
        finally
        {
            _statusBusy = false;
        }
    }
}
