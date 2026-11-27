using System.Text.Json;
using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>Events (tournaments) on the lock screen: the booking before the start, the time left while it runs, the session line.</summary>
public sealed class EventNoticeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 16, 0, 0, TimeSpan.Zero);

    private static ClientReservationInfo Booking(string name = "CS2 Cup 5×5") => new()
    {
        Id = 4, Name = name, StartUtc = Start, StartLocal = "18:00", IsEvent = true, EndUtc = Start.AddHours(3)
    };

    [Fact]
    public void Before_the_start_the_lock_screen_names_the_event_and_its_start()
    {
        Assert.Equal("> ПОДІЯ «CS2 Cup 5×5» О 18:00", ReservationNotice.LockLine(Booking(), LauncherTexts.Uk, Start.AddMinutes(-10)));
        Assert.Equal("> EVENT «CS2 Cup 5×5» AT 18:00", ReservationNotice.LockLine(Booking(), LauncherTexts.En, Start.AddMinutes(-10)));
    }

    [Fact]
    public void While_the_event_runs_the_lock_screen_shows_the_time_left_until_the_common_end()
    {
        Assert.Equal("> EVENT «CS2 Cup 5×5»: 03:00:00 LEFT", ReservationNotice.LockLine(Booking(), LauncherTexts.En, Start));
        Assert.Equal("> ПОДІЯ «CS2 Cup 5×5»: ЗАЛИШИЛОСЬ 01:29:59", ReservationNotice.LockLine(Booking(), LauncherTexts.Uk, Start.AddMinutes(90).AddSeconds(1)));
        Assert.Null(ReservationNotice.LockLine(Booking(), LauncherTexts.En, Start.AddHours(3))); // over
        // A booking without an end (an older server) keeps the start line
        var noEnd = new ClientReservationInfo { Name = "Cup", StartUtc = Start, StartLocal = "18:00", IsEvent = true };
        Assert.Equal("> EVENT «Cup» AT 18:00", ReservationNotice.LockLine(noEnd, LauncherTexts.En, Start.AddHours(1)));
    }

    [Fact]
    public void A_player_of_the_event_sees_the_event_in_the_session_panel()
    {
        var session = new ClientSessionInfo { Id = Guid.NewGuid(), Tariff = "Standard", Event = new ClientEventInfo { Id = 4, Name = "Нічний\nтурнір", EndUtc = Start } };
        Assert.Equal("🏆 Подія: Нічнийтурнір", EventNotice.SessionLine(session, LauncherTexts.Uk));
        Assert.Equal("🏆 Event: Нічнийтурнір", EventNotice.SessionLine(session, LauncherTexts.En));
        Assert.Null(EventNotice.SessionLine(new ClientSessionInfo { Tariff = "Standard" }, LauncherTexts.En));
        Assert.Null(EventNotice.SessionLine(null, LauncherTexts.En));
        Assert.Equal(EventNotice.MaxName, EventNotice.CleanName(new string('x', 99)).Length);
        Assert.Equal("?", EventNotice.CleanName("\t"));
    }

    [Fact]
    public void A_login_on_an_event_pc_says_which_event_and_when()
    {
        var res = new PipeResponse(409, "{\"code\":\"pc_event\",\"args\":[\"CS2 Cup\",\"18:00\",\"4\"]}");
        Assert.Equal("This PC is booked for the event «CS2 Cup» at 18:00. The players of the event play from its start; ask the staff.", LoginMessages.For(res, LauncherTexts.En));
        Assert.StartsWith("Цей ПК зайнятий для події «CS2 Cup» о 18:00.", LoginMessages.For(res, LauncherTexts.Uk));
    }

    [Fact]
    public void The_event_fields_of_the_heartbeat_reach_the_lock_screen_through_state_json()
    {
        // The agent copies the objects of the heartbeat into state.json with the shared classes (camelCase)
        var json = "{\"isEvent\":true,\"name\":\"Cup\",\"startLocal\":\"18:00\",\"startUtc\":\"2026-11-23T16:00:00+00:00\",\"endUtc\":\"2026-11-23T19:00:00+00:00\",\"id\":4}";
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var info = JsonSerializer.Deserialize<ClientReservationInfo>(json, options)!;
        var again = JsonSerializer.Deserialize<ClientReservationInfo>(JsonSerializer.Serialize(info, options), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(again.IsEvent);
        Assert.Equal(Start.AddHours(3), again.EndUtc);
        var session = JsonSerializer.Deserialize<ClientSessionInfo>("{\"event\":{\"id\":4,\"name\":\"Cup\",\"endUtc\":\"2026-11-23T19:00:00+00:00\"}}", options)!;
        Assert.Equal("Cup", session.Event!.Name);
    }
}
