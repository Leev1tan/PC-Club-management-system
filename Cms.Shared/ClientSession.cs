namespace Cms.Shared;

/// <summary>
/// The active session of a PC as the lock screen shows it: the server sends it with every heartbeat
/// response, the agent copies it into state.json (display only; the agent locks the PC by the session_set
/// commands). Null when the PC has no active session.
/// </summary>
public sealed class ClientSessionInfo
{
    public Guid Id { get; init; }
    public DateTimeOffset StartUtc { get; init; }
    /// <summary>Planned end: prepaid and package end, or when the balances run out (FromBalance); null = open-ended.</summary>
    public DateTimeOffset? EndUtc { get; init; }
    public bool Paused { get; init; }
    public bool Prepaid { get; init; }
    /// <summary>Started by the player "from the balance": ends when the balances are used up.</summary>
    public bool FromBalance { get; init; }
    public string? Tariff { get; init; }
    public string? Package { get; init; }
    /// <summary>Registered player of the session (null = a guest of the desk).</summary>
    public string? Username { get; init; }
    public string? DisplayName { get; init; }
    public decimal? Balance { get; init; }
    public decimal? Bonus { get; init; }
}

/// <summary>
/// "Log off" from the admin UI (command logoff). The agent runs as a service in session 0 and cannot log off the
/// player's Windows session, so it puts this request into state.json for about a minute; the lock screen, which
/// runs in the player's session, logs that session off once (Cms.Client.LogoffTrigger).
/// </summary>
public sealed class LogoffRequest
{
    public Guid Id { get; init; }
    public DateTimeOffset RequestedUtc { get; init; }
}
