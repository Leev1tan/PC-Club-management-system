using Cms.Shared;

namespace Cms.Client;

/// <summary>
/// Decides when the lock screen logs off the Windows session for a "log off" command of the staff (the agent
/// forwards it in state.json, see Cms.Shared.LogoffRequest). A request is followed once, only when it was made
/// after this lock screen started (a request left in state.json must not log off the next player who signs in)
/// and only while it is fresh (at most a minute old; the clocks of the agent and the lock screen are the same PC).
/// </summary>
public sealed class LogoffTrigger
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private readonly DateTimeOffset _startedUtc;
    private readonly HashSet<Guid> _handled = new();

    public LogoffTrigger(DateTimeOffset startedUtc) => _startedUtc = startedUtc;

    /// <summary>True once for a new, fresh request made after the start of this lock screen.</summary>
    public bool ShouldLogOff(LogoffRequest? request, DateTimeOffset now)
    {
        if (request == null || request.Id == Guid.Empty || _handled.Contains(request.Id)) return false;
        if (request.RequestedUtc < _startedUtc) { _handled.Add(request.Id); return false; }
        var age = now - request.RequestedUtc;
        if (age > MaxAge || age < -MaxAge) { _handled.Add(request.Id); return false; }
        _handled.Add(request.Id);
        return true;
    }
}
