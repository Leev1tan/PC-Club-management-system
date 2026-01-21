using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Repositories;

public class EfSessionRepository : ISessionRepository
{
    private readonly CmsDbContext _db;

    public EfSessionRepository(CmsDbContext db)
    {
        _db = db;
    }

    public SessionRecord Start(Guid deviceId, TimeSpan duration)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new SessionEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            StartUtc = now,
            EndUtc = now.Add(duration)
        };
        _db.Sessions.Add(entity);
        _db.SaveChanges();
        return new SessionRecord(entity.Id, entity.DeviceId, entity.StartUtc, entity.EndUtc);
    }

    public SessionRecord? End(Guid sessionId)
    {
        var entity = _db.Sessions.Find(sessionId);
        if (entity == null) return null;
        entity.EndUtc = DateTimeOffset.UtcNow;
        _db.SaveChanges();
        return new SessionRecord(entity.Id, entity.DeviceId, entity.StartUtc, entity.EndUtc);
    }

    public IEnumerable<SessionRecord> List()
    {
        return _db.Sessions.AsNoTracking().OrderByDescending(s => s.StartUtc)
            .Select(s => new SessionRecord(s.Id, s.DeviceId, s.StartUtc, s.EndUtc));
    }
}

