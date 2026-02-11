namespace Cms.Server.Repositories;

// Session record kept for backward compatibility with ISessionRepository consumers
public record SessionRecord(Guid Id, Guid DeviceId, DateTimeOffset StartUtc, DateTimeOffset? EndUtc);

public interface ISessionRepository
{
    SessionRecord Start(Guid deviceId, TimeSpan duration);
    SessionRecord? End(Guid sessionId);
    IEnumerable<SessionRecord> List();
}
