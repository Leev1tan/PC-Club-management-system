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
    /// <summary>The bar order of this session: open, or closed in the last 15 minutes (the lock screen shows its state).</summary>
    public ClientOrderInfo? Order { get; init; }
    /// <summary>The event (tournament) of this session: its name and the common end; null = a normal session.</summary>
    public ClientEventInfo? Event { get; init; }
}

/// <summary>An event (tournament) on the lock screen: the name that staff gave it and the common end of its sessions.</summary>
public sealed class ClientEventInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset EndUtc { get; init; }
}

/// <summary>A bar order on the lock screen: new, preparing, delivered or cancelled; paid from the balance or at the desk.</summary>
public sealed class ClientOrderInfo
{
    public long Id { get; init; }
    public string Status { get; init; } = "new";
    public string Payment { get; init; } = "desk";
    public decimal Total { get; init; }
    public int Items { get; init; }
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

/// <summary>
/// The reservation that holds the PC now (heartbeat response, then state.json): the lock screen shows "Reserved for
/// {Name} at {StartLocal}". Name is the nickname of a player or the first name of a guest: never a phone or a surname.
/// For an event (IsEvent) Name is the name of the event: "Event «{Name}» at {StartLocal}", and while the event runs
/// the time left until EndUtc.
/// </summary>
public sealed class ClientReservationInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset StartUtc { get; init; }
    /// <summary>Start in club time, "HH:mm".</summary>
    public string StartLocal { get; init; } = string.Empty;
    /// <summary>The PC is booked for an event (tournament), not for a reservation.</summary>
    public bool IsEvent { get; init; }
    /// <summary>Events: the common end.</summary>
    public DateTimeOffset? EndUtc { get; init; }
}
