using System.IO;
using System.Windows;
using Cms.Client;
using Cms.Shared;

namespace Cms.Launcher;

/// <summary>
/// Clean session teardown (Cms.Client.SessionTeardown): when the session of this PC is over, the lock
/// screen kills the processes it started (with their child processes), the processes of the games it
/// started (their process names) and the configured names (launchers, chat, browsers), only in the
/// player's Windows session, then deletes the configured per-user paths when the cleanup is on. The
/// settings come from teardown.json (written by the agent from its appsettings.json on this PC). What it
/// did goes to the agent inbox and from there to the audit log (device.session_teardown).
/// </summary>
public partial class MainWindow
{
    private readonly TeardownTrigger _teardownTrigger = new();
    private readonly List<StartedProcess> _startedProcesses = new();
    private readonly HashSet<string> _startedGameProcesses = new(StringComparer.OrdinalIgnoreCase);
    private bool _teardownRunning;

    private void RememberStarted(CatalogGame game, int? pid, string? processName)
    {
        if (pid is { } id && !string.IsNullOrEmpty(processName)) _startedProcesses.Add(new StartedProcess(id, processName));
        if (!string.IsNullOrEmpty(game.ProcessName)) _startedGameProcesses.Add(game.ProcessName);
    }

    /// <summary>Called on every state read: runs the teardown once when the session of this PC is over.</summary>
    private void CheckTeardown(LockDecision decision, LauncherState? state)
    {
        var agentAlive = state != null && decision.Reason is not (LockReason.AgentSilent or LockReason.NoAgentState);
        if (_teardownTrigger.Update(decision.Locked && !decision.StaffOverride, agentAlive, state?.LockReason, state?.Session?.Id))
            RunTeardown();
    }

    private TeardownSettings ReadTeardownSettings()
    {
        try
        {
            var path = Path.Combine(AgentFiles.Directory, AgentFiles.TeardownFile);
            if (File.Exists(path) && IsTrustedAgentFile(path)) return TeardownSettings.Parse(ReadShared(path)) ?? TeardownSettings.Default;
        }
        catch
        {
            // unreadable: the defaults (cleanup off)
        }
        return TeardownSettings.Default;
    }

    private void RunTeardown()
    {
        if (_teardownRunning) return;
        _teardownRunning = true;
        ReportGameEvents(_gameTracker.StopAll(DateTimeOffset.UtcNow));
        GameNowText.Text = string.Empty;
        var started = _startedProcesses.ToList();
        var gameNames = _startedGameProcesses.ToList();
        _startedProcesses.Clear();
        _startedGameProcesses.Clear();
        var settings = ReadTeardownSettings();

        Task.Run(() =>
        {
            var plan = ProcessTeardown.Plan(_processes.Snapshot(), started, settings.KillProcessNames.Concat(gameNames),
                _processes.CurrentSessionId, _processes.CurrentPid);
            var (killed, failed) = ProcessTeardown.Kill(_processes, plan);
            // %VARIABLES% of the player's profile (this program runs as the player)
            var cleanup = DataCleanup.Run(new SystemFileSystem(), settings.Cleanup, Environment.ExpandEnvironmentVariables);
            return new TeardownReport(killed, failed, cleanup);
        }).ContinueWith(task => Dispatcher.Invoke(() =>
        {
            _teardownRunning = false;
            if (!task.IsCompletedSuccessfully) return;
            var r = task.Result;
            WriteInboxEvent(new LauncherEvent
            {
                Type = LauncherEvent.SessionTeardown,
                WindowsUser = Environment.UserName,
                Killed = r.Killed.Take(30).ToArray(),
                KillFailed = r.KillFailed.Count,
                CleanupEnabled = settings.Cleanup.Enabled,
                RemovedFiles = r.Cleanup.RemovedFiles,
                RemovedDirectories = r.Cleanup.RemovedDirectories,
                RemovedPaths = r.Cleanup.RemovedPaths.Take(15).ToArray(),
                CleanupErrors = r.Cleanup.Errors.Count
            });
        }));
    }
}
