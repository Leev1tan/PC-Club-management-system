using Cms.Server.Controllers;
using Cms.Server.Data;
using Cms.Server.Repositories;
using Cms.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

public sealed class SessionExtendTests : IDisposable
{
    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    private async Task<(SessionEntity Session, UserEntity User)> CreateSessionAsync(bool prepaid)
    {
        await using var db = _testDb.NewContext();
        var tariff = new TariffEntity { Id = Guid.NewGuid(), Name = "Standard", PricePerHour = 60m, IsDefault = true };
        var device = new DeviceEntity { Id = Guid.NewGuid(), Hostname = "PC-01", DeviceKey = "key-1" };
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "player", DisplayName = "Player", Balance = 100m };
        var now = DateTimeOffset.UtcNow;
        var session = new SessionEntity
        {
            Id = Guid.NewGuid(), DeviceId = device.Id, TariffId = tariff.Id, UserId = user.Id,
            StartUtc = now.AddMinutes(-20), EndUtc = prepaid ? now.AddMinutes(10) : null,
            Status = "active", TotalCost = prepaid ? 30m : 0m, IsPrepaid = prepaid
        };
        db.AddRange(tariff, device, user, session);
        await db.SaveChangesAsync();
        return (session, user);
    }

    [Fact]
    public async Task Extend_moves_the_end_time_charges_the_player_and_notifies_the_agent()
    {
        var (session, user) = await CreateSessionAsync(prepaid: true);

        await using var db = _testDb.NewContext();
        var controller = new SessionsController(db, new EfDeviceRepository(db));
        var ok = Assert.IsType<OkObjectResult>((await controller.Extend(session.Id, new ExtendSessionRequest(30))).Result);
        var view = Assert.IsType<SessionDetailView>(ok.Value);

        Assert.Equal(60m, view.TotalCost);
        Assert.True(view.EndUtc >= session.EndUtc!.Value.AddMinutes(30));
        Assert.Equal(70m, (await db.Users.SingleAsync(u => u.Id == user.Id)).Balance);
        Assert.Contains(await db.Commands.ToListAsync(), c => c.Type == CommandTypes.SessionSet && c.Status == "pending");
    }

    [Fact]
    public async Task Extend_rejects_open_ended_sessions_and_bad_input()
    {
        var (session, _) = await CreateSessionAsync(prepaid: false);

        await using var db = _testDb.NewContext();
        var controller = new SessionsController(db, new EfDeviceRepository(db));
        Assert.IsType<BadRequestObjectResult>((await controller.Extend(session.Id, new ExtendSessionRequest(30))).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.Extend(session.Id, new ExtendSessionRequest(0))).Result);
        Assert.IsType<NotFoundResult>((await controller.Extend(Guid.NewGuid(), new ExtendSessionRequest(30))).Result);
    }
}
