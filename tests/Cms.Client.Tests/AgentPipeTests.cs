using System.IO.Pipes;
using System.Text;
using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>The pipe protocol over a real named pipe (a Unix domain socket on Linux).</summary>
public sealed class AgentPipeTests
{
    private static string NewName() => "ccms-test-" + Guid.NewGuid().ToString("N")[..12];

    private static NamedPipeServerStream Create(string name) =>
        new(name, PipeDirection.InOut, AgentPipe.MaxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static (Task Run, CancellationTokenSource Stop) Serve(string name, Func<PipeRequest, CancellationToken, Task<PipeResponse>> handler)
    {
        var stop = new CancellationTokenSource();
        var server = new AgentPipeServer(_ => Create(name), handler);
        return (Task.Run(() => server.RunAsync(stop.Token)), stop);
    }

    [Fact]
    public async Task A_request_gets_the_answer_of_the_handler()
    {
        var name = NewName();
        PipeRequest? seen = null;
        var (run, stop) = Serve(name, (req, _) =>
        {
            seen = req;
            return Task.FromResult(new PipeResponse(200, """{"sessionId":"4b1f3f0e-0000-0000-0000-000000000001","username":"vika","displayName":"Віка","balance":12.5,"bonus":0,"endUtc":null,"fromBalance":true,"offer":"balance"}"""));
        });
        var package = Guid.NewGuid();
        var res = await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Login, "vika", "1357", "package", null, package), TimeSpan.FromSeconds(5));
        Assert.True(res.Ok);
        Assert.Equal(("vika", "1357", "package", package), (seen!.Username, seen.Secret, seen.Offer, seen.PackageId));
        var login = res.As<LoginResult>()!;
        Assert.Equal(("Віка", 12.5m, true), (login.DisplayName, login.Balance, login.FromBalance));

        // Several requests one after the other on the same pipe name
        for (var i = 0; i < 3; i++)
            Assert.True((await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Offers), TimeSpan.FromSeconds(5))).Ok);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Unknown_or_broken_requests_are_refused_and_handler_errors_are_hidden()
    {
        var name = NewName();
        var (run, stop) = Serve(name, (req, _) => req.Op == PipeOps.Logout ? throw new InvalidOperationException("secret details") : Task.FromResult(new PipeResponse(204, null)));

        var unknown = await AgentPipeClient.SendAsync(name, new PipeRequest("format_disk"), TimeSpan.FromSeconds(5));
        Assert.Equal((400, "bad_request"), (unknown.Status, unknown.Code));
        var failing = await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Logout), TimeSpan.FromSeconds(5));
        Assert.Equal((500, "agent_error"), (failing.Status, failing.Code));
        Assert.DoesNotContain("secret", failing.Body);

        // Raw garbage and an oversized line get no answer and do not stop the server
        foreach (var payload in new[] { "not json\n", new string('x', AgentPipe.MaxMessageBytes + 10) + "\n" })
        {
            await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await raw.ConnectAsync(5000);
            var buffer = new byte[256];
            int n;
            try
            {
                await raw.WriteAsync(Encoding.UTF8.GetBytes(payload));
                await raw.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                n = await raw.ReadAsync(buffer, cts.Token);
            }
            catch (IOException)
            {
                n = 0; // the server closed with unread data: a reset is "no answer" too
            }
            var answer = Encoding.UTF8.GetString(buffer, 0, n);
            Assert.True(n == 0 || answer.Contains("bad_request"), answer);
        }
        Assert.Equal(204, (await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Offers), TimeSpan.FromSeconds(5))).Status);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task No_agent_or_a_refused_server_means_agent_unavailable()
    {
        var res = await AgentPipeClient.SendAsync(NewName(), new PipeRequest(PipeOps.Offers), TimeSpan.FromMilliseconds(300));
        Assert.Equal((503, "agent_unavailable"), (res.Status, res.Code));

        var name = NewName();
        var called = false;
        var (run, stop) = Serve(name, (_, _) => { called = true; return Task.FromResult(new PipeResponse(200, "{}")); });
        var refused = await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Login, "a", "b"), TimeSpan.FromSeconds(5), verify: _ => false);
        Assert.Equal((503, "agent_untrusted"), (refused.Status, refused.Code));
        Assert.False(called); // the secret was never sent to an untrusted pipe server
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Many_requests_in_a_row_all_get_their_answer()
    {
        // A handler that answers at once: the server serves the connection synchronously, the client asks again at once.
        // The next instance must listen before the served one closes, or the name of the pipe goes away for a moment
        // (on Unix its socket closes with its last instance) and a client that connected then gets a reset.
        var name = NewName();
        var (run, stop) = Serve(name, (req, _) => Task.FromResult(new PipeResponse(200, "{\"n\":1}")));
        var failed = 0;
        for (var i = 0; i < 300; i++)
            if (!(await AgentPipeClient.SendAsync(name, new PipeRequest(PipeOps.Offers), TimeSpan.FromSeconds(5))).Ok) failed++;
        Assert.Equal(0, failed);
        stop.Cancel();
        await run;
    }

    [Fact]
    public void Requests_are_parsed_strictly()
    {
        Assert.Null(AgentPipe.ParseRequest(null));
        Assert.Null(AgentPipe.ParseRequest("{\"op\":\"shutdown\"}"));
        Assert.Null(AgentPipe.ParseRequest("[]"));
        Assert.Equal(PipeOps.Offers, AgentPipe.ParseRequest("{\"op\":\"offers\"}")!.Op);
    }
}

public sealed class LowTimeWarningsTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_warning_comes_once_at_10_5_and_1_minute()
    {
        var w = new LowTimeWarnings();
        Assert.Null(w.Check(End, End.AddMinutes(-60))); // the end is new
        var fired = new List<int>();
        for (var t = End.AddMinutes(-60); t < End.AddMinutes(1); t = t.AddSeconds(5))
            if (w.Check(End, t) is { } m) fired.Add(m);
        Assert.Equal(new[] { 10, 5, 1 }, fired);
    }

    [Fact]
    public void A_new_end_skips_the_warnings_behind_and_keeps_those_ahead()
    {
        var w = new LowTimeWarnings();
        var now = End.AddMinutes(-7); // a session that starts with 7 minutes left: no "10 min"
        Assert.Null(w.Check(End, now));
        Assert.Null(w.Check(End, now.AddMinutes(1)));
        Assert.Equal(5, w.Check(End, End.AddMinutes(-5)));
        // Extended by 30 minutes with 4 minutes left: all three come again
        var later = End.AddMinutes(30);
        Assert.Null(w.Check(later, End.AddMinutes(-4)));
        Assert.Null(w.Check(later, later.AddMinutes(-11)));
        Assert.Equal(10, w.Check(later, later.AddMinutes(-10)));
        // Open-ended (no end): nothing
        Assert.Null(w.Check(null, End));
        Assert.Null(w.Check(null, End.AddMinutes(1)));
    }
}

public sealed class LoginMessagesTests
{
    [Theory]
    [InlineData(401, "login_failed", null, "WrongLogin")]
    [InlineData(429, "login_locked", "15", "LoginLocked")]
    [InlineData(400, "insufficient_balance", null, "InsufficientBalance")]
    [InlineData(409, "player_has_debt", null, "PlayerHasDebt")]
    [InlineData(409, "player_busy", "VIP-02", "PlayerBusy")]
    [InlineData(409, "device_busy", null, "DeviceBusy")]
    [InlineData(400, "package_outside_window", null, "PackageUnavailable")]
    [InlineData(400, "no_tariff", null, "NoTariff")]
    [InlineData(503, "agent_untrusted", null, "AgentUnavailable")]
    [InlineData(500, "something_new", null, "ServerError")]
    public void Every_answer_has_a_text_in_both_languages(int status, string code, string? arg, string text)
    {
        var body = arg == null ? $"{{\"code\":\"{code}\"}}" : $"{{\"code\":\"{code}\",\"args\":[\"{arg}\"]}}";
        var response = new PipeResponse(status, body);
        foreach (var t in new[] { LauncherTexts.Uk, LauncherTexts.En })
        {
            var expected = t.All()[text];
            var message = LoginMessages.For(response, t);
            Assert.Equal(expected.Contains("{0}") ? LauncherTexts.Format(expected, arg ?? status.ToString()) : expected, message);
        }
    }
}
