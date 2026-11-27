using System.Net;
using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>
/// Windows test 2026-11-27, defect 1: the agent stopped for good when it started while the server did not answer. The
/// HttpClient timeout (TaskCanceledException) left the registration retry loop and stopped the service host.
/// </summary>
public sealed class AgentLoopsTests
{
    /// <summary>A server that never answers: every request waits until HttpClient gives up (its Timeout).</summary>
    private sealed class SilentServer : HttpMessageHandler
    {
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Fails a number of times (connection refused), then answers 200.</summary>
    private sealed class FlakyServer(int failures) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Requests) <= failures) throw new HttpRequestException("Connection refused");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://club-server:5081/"), Timeout = TimeSpan.FromMilliseconds(50) };

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task A_server_that_times_out_never_ends_the_registration_retry()
    {
        var server = new SilentServer();
        using var http = Client(server);
        using var stop = new CancellationTokenSource();
        var failures = new List<Exception>();

        var task = AgentLoops.RetryAsync(
            async ct => (await http.PostAsync("api/devices/register", new StringContent("{}"), ct)).EnsureSuccessStatusCode(),
            (ex, _) => { failures.Add(ex); if (failures.Count == 5) stop.Cancel(); },
            retryForever: true, maxAttempts: 3, Short, TimeSpan.FromMilliseconds(20), stop.Token);

        var registered = await task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(registered);                                   // ended by the stop of the service, not by an exception
        Assert.Equal(5, failures.Count);
        Assert.All(failures, ex => Assert.IsAssignableFrom<OperationCanceledException>(ex)); // the HttpClient timeout
        Assert.All(failures, ex => Assert.Equal(AgentLoops.ErrorKind.Timeout, AgentLoops.Classify(ex)));
        Assert.Equal(5, server.Requests);
    }

    [Fact]
    public async Task Without_retry_forever_a_timeout_gives_false_after_the_attempts_and_never_throws()
    {
        using var http = Client(new SilentServer());
        var calls = 0;
        var result = await AgentLoops.RetryAsync(
            async ct => { calls++; (await http.GetAsync("health", ct)).EnsureSuccessStatusCode(); },
            (_, _) => { }, retryForever: false, maxAttempts: 3, Short, Short, CancellationToken.None);
        Assert.False(result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task The_retry_succeeds_when_the_server_comes_back_and_the_delay_doubles_up_to_the_maximum()
    {
        var server = new FlakyServer(failures: 4);
        using var http = Client(server);
        var delays = new List<TimeSpan>();
        var result = await AgentLoops.RetryAsync(
            async ct => (await http.GetAsync("health", ct)).EnsureSuccessStatusCode(),
            (ex, delay) => { Assert.Equal(AgentLoops.ErrorKind.Network, AgentLoops.Classify(ex)); delays.Add(delay); },
            retryForever: true, maxAttempts: 3, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(8), CancellationToken.None);
        Assert.True(result);
        Assert.Equal(5, server.Requests);
        Assert.Equal(new[] { 2, 4, 8, 8 }.Select(ms => TimeSpan.FromMilliseconds(ms)), delays);
    }

    [Fact]
    public async Task A_stop_during_the_wait_ends_the_retry_without_an_exception()
    {
        using var stop = new CancellationTokenSource();
        var result = AgentLoops.RetryAsync(_ => throw new HttpRequestException("down"), (_, _) => stop.Cancel(),
            retryForever: true, maxAttempts: 3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), stop.Token);
        Assert.False(await result.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_loop_body_that_throws_a_timeout_or_any_error_is_started_again_until_the_service_stops()
    {
        using var http = Client(new SilentServer());
        using var stop = new CancellationTokenSource();
        var runs = 0;
        var restarts = new List<Exception?>();

        var loop = AgentLoops.RunForeverAsync(async ct =>
        {
            runs++;
            switch (runs)
            {
                case 1: await http.GetAsync("api/devices/heartbeat", ct); break;  // HttpClient timeout
                case 2: throw new InvalidOperationException("a bug");
                case 3: throw new OperationCanceledException();                    // not the stop token
                case 4: return;                                                   // the body ended by itself
                default: stop.Cancel(); await Task.Delay(Timeout.Infinite, ct); break;
            }
        }, restarts.Add, Short, stop.Token);

        await loop.WaitAsync(TimeSpan.FromSeconds(30)); // returns, never throws
        Assert.Equal(5, runs);
        Assert.Equal(4, restarts.Count);
        Assert.IsAssignableFrom<OperationCanceledException>(restarts[0]);
        Assert.IsType<InvalidOperationException>(restarts[1]);
        Assert.IsType<OperationCanceledException>(restarts[2]);
        Assert.Null(restarts[3]);
    }

    [Fact]
    public async Task A_logger_that_throws_does_not_end_the_loop()
    {
        using var stop = new CancellationTokenSource();
        var runs = 0;
        await AgentLoops.RunForeverAsync(_ =>
        {
            if (++runs == 3) stop.Cancel();
            throw new HttpRequestException("down");
        }, _ => throw new IOException("log file full"), Short, stop.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, runs);

        var attempts = 0;
        var ok = await AgentLoops.RetryAsync(_ => ++attempts < 3 ? throw new HttpRequestException("down") : Task.CompletedTask,
            (_, _) => throw new IOException("log file full"), true, 3, Short, Short, CancellationToken.None);
        Assert.True(ok);
    }

    [Fact]
    public void Errors_are_classified_for_the_log()
    {
        Assert.Equal(AgentLoops.ErrorKind.Timeout, AgentLoops.Classify(new TaskCanceledException()));
        Assert.Equal(AgentLoops.ErrorKind.Timeout, AgentLoops.Classify(new TimeoutException()));
        Assert.Equal(AgentLoops.ErrorKind.Network, AgentLoops.Classify(new HttpRequestException("refused")));
        Assert.Equal(AgentLoops.ErrorKind.Network, AgentLoops.Classify(new System.Net.Sockets.SocketException()));
        Assert.Equal(AgentLoops.ErrorKind.Other, AgentLoops.Classify(new UriFormatException()));
    }
}
