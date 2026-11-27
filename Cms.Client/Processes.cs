using System.Diagnostics;

namespace Cms.Client;

/// <summary>A process of the PC. SessionId is the Windows logon session (only processes of the player's session are touched).</summary>
public sealed record ProcessInfo(int Pid, string Name, int SessionId, int ParentPid = 0, DateTime? StartTime = null);

/// <summary>The process list of the PC, behind an interface so that the game tracker and the teardown can be tested with a fake list.</summary>
public interface IProcessTable
{
    /// <summary>All processes that the caller can see.</summary>
    IReadOnlyList<ProcessInfo> Snapshot();

    /// <summary>Kills the process, with its child processes when entireTree is true. False when it is gone or access is denied.</summary>
    bool Kill(int pid, bool entireTree);

    /// <summary>Logon session of this program (the lock screen runs in the player's session).</summary>
    int CurrentSessionId { get; }

    /// <summary>Id of this program, never killed.</summary>
    int CurrentPid { get; }
}

/// <summary>The real process list (System.Diagnostics.Process; works on Windows and, for the tests, on Linux).</summary>
public sealed class SystemProcessTable : IProcessTable
{
    public int CurrentSessionId { get; } = Process.GetCurrentProcess().SessionId;
    public int CurrentPid { get; } = Environment.ProcessId;

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    DateTime? start = null;
                    try { start = p.StartTime; } catch { /* access denied for processes of other users */ }
                    list.Add(new ProcessInfo(p.Id, p.ProcessName, p.SessionId, 0, start));
                }
                catch (InvalidOperationException)
                {
                    // exited meanwhile
                }
            }
        }
        return list;
    }

    public bool Kill(int pid, bool entireTree)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireTree);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false; // gone, or not ours
        }
    }
}
