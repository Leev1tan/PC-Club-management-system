using System.Net.Sockets;

namespace Cms.Client;

/// <summary>
/// The rules that keep the agent service (Cms.Agent.Service) running. The agent must never stop because of a server,
/// network or timeout error: the server PC can be off, the network can be down, a request can time out. Only the stop
/// of the service (the stop token) ends its loops. Without Windows APIs, so it is tested on any OS.
/// Note: HttpClient reports its timeout as a TaskCanceledException (an OperationCanceledException). Such an exception
/// means "stop" only when the stop token is cancelled; at any other time it is a timeout to log and retry.
/// </summary>
public static class AgentLoops
{
    public enum ErrorKind
    {
        /// <summary>A request timed out (an OperationCanceledException while the service does not stop).</summary>
        Timeout,
        /// <summary>The server cannot be reached (connection refused, no route, DNS, a broken connection).</summary>
        Network,
        /// <summary>Anything else (a bad answer, a bug): logged with the exception, then tried again.</summary>
        Other
    }

    /// <summary>What kind of error this is, for the log line. Every kind is tried again.</summary>
    public static ErrorKind Classify(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => ErrorKind.Timeout,
        HttpRequestException or SocketException or IOException => ErrorKind.Network,
        _ => ErrorKind.Other
    };

    /// <summary>
    /// Runs the body until the stop token is cancelled. When the body throws (any exception) or returns while the service
    /// does not stop, onRestart gets the exception (null: the body returned) and the body runs again after restartDelay.
    /// Returns only when the stop token is cancelled; never throws.
    /// </summary>
    public static async Task RunForeverAsync(Func<CancellationToken, Task> body, Action<Exception?> onRestart, TimeSpan restartDelay, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Exception? error = null;
            try
            {
                await body(stop).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            if (stop.IsCancellationRequested) return;
            SafeInvoke(() => onRestart(error));
            if (!await DelayAsync(restartDelay, stop).ConfigureAwait(false)) return;
        }
    }

    /// <summary>
    /// Calls the attempt until it succeeds (true). A failed attempt (any exception, also a timeout) goes to onFailure with
    /// the delay before the next attempt; the delay starts at firstDelay and doubles up to maxDelay. With retryForever false
    /// the result is false after maxAttempts failures (the agent then looks for the server again). False when the stop
    /// token is cancelled. Never throws.
    /// </summary>
    public static async Task<bool> RetryAsync(Func<CancellationToken, Task> attempt, Action<Exception, TimeSpan> onFailure, bool retryForever,
        int maxAttempts, TimeSpan firstDelay, TimeSpan maxDelay, CancellationToken stop)
    {
        var delay = firstDelay;
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await attempt(stop).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                failures++;
                var last = !retryForever && failures >= maxAttempts;
                var wait = delay;
                SafeInvoke(() => onFailure(ex, last ? TimeSpan.Zero : wait));
                if (last) return false;
            }
            catch (Exception)
            {
                return false; // the service stops
            }
            if (!await DelayAsync(delay, stop).ConfigureAwait(false)) return false;
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }
        return false;
    }

    /// <summary>Waits; false when the stop token is cancelled during the wait (no exception).</summary>
    public static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>A log call must never end a loop.</summary>
    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch
        {
            // the logger failed (disk full, event log trouble): the loop goes on
        }
    }
}
