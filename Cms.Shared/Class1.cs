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
/// Represents the agent state persisted to state.json for the Launcher to read.
/// </summary>
public class AgentState
{
    public bool IsLocked { get; set; }
    public long RemainingSeconds { get; set; }
}
