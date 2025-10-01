using System.Collections.Concurrent;

namespace Cms.Server.Repositories;

public class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<Guid, SessionRecord> _sessions = new();

    public SessionRecord Start(Guid deviceId, TimeSpan duration)
    {
        var now = DateTimeOffset.UtcNow;
        var rec = new SessionRecord(Guid.NewGuid(), deviceId, now, now.Add(duration));
        _sessions[rec.Id] = rec;
        return rec;
    }

    public SessionRecord? End(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var rec)) return null;
        var ended = rec with { EndUtc = DateTimeOffset.UtcNow };
        _sessions[sessionId] = ended;
        return ended;
    }

    public IEnumerable<SessionRecord> List() => _sessions.Values.OrderByDescending(s => s.StartUtc);
}

