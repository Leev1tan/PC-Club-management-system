using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

public sealed class ReservationNoticeTests
{
    [Fact]
    public void The_lock_screen_names_the_reservation_in_the_club_language()
    {
        var info = new ClientReservationInfo { Id = 7, Name = "Тарас", StartLocal = "18:00", StartUtc = DateTimeOffset.UtcNow };
        Assert.Equal("> ЗАБРОНЬОВАНО ДЛЯ Тарас НА 18:00", ReservationNotice.LockLine(info, LauncherTexts.Uk));
        Assert.Equal("> RESERVED FOR Тарас AT 18:00", ReservationNotice.LockLine(info, LauncherTexts.En));
        Assert.Null(ReservationNotice.LockLine(null, LauncherTexts.En));
        Assert.Null(ReservationNotice.LockLine(new ClientReservationInfo { Name = "x" }, LauncherTexts.En));
    }

    [Fact]
    public void Names_are_short_and_without_control_characters()
    {
        Assert.Equal("?", ReservationNotice.CleanName(" \t "));
        Assert.Equal("abc", ReservationNotice.CleanName("a\nb\u0007c"));
        Assert.Equal(20, ReservationNotice.CleanName(new string('x', 50)).Length);
    }

    [Fact]
    public void A_login_on_a_held_pc_says_for_whom_and_when()
    {
        var res = new PipeResponse(409, "{\"code\":\"pc_reserved\",\"args\":[\"kozak_pro\",\"19:30\",\"12\"]}");
        Assert.Equal("This PC is reserved for kozak_pro at 19:30. Only the player of the reservation can log in; ask the staff.", LoginMessages.For(res, LauncherTexts.En));
        Assert.StartsWith("Цей ПК заброньовано для kozak_pro на 19:30.", LoginMessages.For(res, LauncherTexts.Uk));
        Assert.Equal("19:30", res.Arg(1));
        Assert.Null(res.Arg(5));
    }
}
