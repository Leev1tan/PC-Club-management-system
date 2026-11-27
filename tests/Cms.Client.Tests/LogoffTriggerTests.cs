using Cms.Client;
using Cms.Shared;
using Xunit;

namespace Cms.Client.Tests;

public sealed class LogoffTriggerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_request_logs_off_once()
    {
        var trigger = new LogoffTrigger(Start);
        var request = new LogoffRequest { Id = Guid.NewGuid(), RequestedUtc = Start.AddMinutes(5) };
        Assert.False(trigger.ShouldLogOff(null, Start.AddMinutes(5)));
        Assert.True(trigger.ShouldLogOff(request, Start.AddMinutes(5).AddSeconds(2)));
        Assert.False(trigger.ShouldLogOff(request, Start.AddMinutes(5).AddSeconds(4))); // the same request again
        Assert.True(trigger.ShouldLogOff(new LogoffRequest { Id = Guid.NewGuid(), RequestedUtc = Start.AddMinutes(9) }, Start.AddMinutes(9)));
    }

    [Fact]
    public void A_request_from_before_the_start_does_not_log_off_the_next_player()
    {
        // The agent keeps the request in state.json for a minute; Windows signs in again and a new lock screen starts
        var trigger = new LogoffTrigger(Start);
        var old = new LogoffRequest { Id = Guid.NewGuid(), RequestedUtc = Start.AddSeconds(-20) };
        Assert.False(trigger.ShouldLogOff(old, Start.AddSeconds(5)));
        Assert.False(trigger.ShouldLogOff(old, Start.AddSeconds(7)));
    }

    [Fact]
    public void A_stale_or_empty_request_is_ignored()
    {
        var trigger = new LogoffTrigger(Start);
        Assert.False(trigger.ShouldLogOff(new LogoffRequest { Id = Guid.NewGuid(), RequestedUtc = Start.AddMinutes(1) }, Start.AddMinutes(3)));
        Assert.False(trigger.ShouldLogOff(new LogoffRequest { Id = Guid.Empty, RequestedUtc = Start.AddMinutes(1) }, Start.AddMinutes(1)));
        Assert.False(trigger.ShouldLogOff(new LogoffRequest { Id = Guid.NewGuid(), RequestedUtc = Start.AddMinutes(10) }, Start.AddMinutes(2)));
    }
}
