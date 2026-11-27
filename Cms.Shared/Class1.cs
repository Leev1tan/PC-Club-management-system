namespace Cms.Shared;

/// <summary>
/// Supported agent command types.
/// </summary>
public static class CommandTypes
{
    public const string Lock = "lock";
    public const string Unlock = "unlock";
    /// <summary>Reboot of the PC (shutdown /r).</summary>
    public const string Restart = "restart";
    /// <summary>Shutdown of the PC (shutdown /s); Wake-on-LAN starts it again.</summary>
    public const string Shutdown = "shutdown";
    public const string Logoff = "logoff";
    public const string Message = "message";
    public const string SessionSet = "session_set";
}

/// <summary>
/// The payload of an "unlock" command: {"reason":"staff"} = staff unlocked a PC without an active session (no time
/// limit; it ends with a "lock" or the next start of the agent). No payload = an unlock for a session (session start,
/// resume, move, or a staff unlock while a session runs): the end time of the session applies.
/// </summary>
public static class UnlockReasons
{
    public const string Staff = "staff";
}

/// <summary>
/// Represents the agent state persisted to state.json for the Launcher to read.
/// </summary>
public class AgentState
{
    public bool IsLocked { get; set; }
    public long RemainingSeconds { get; set; }
}
