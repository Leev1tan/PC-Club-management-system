using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Cms.Client;

/// <summary>Requests of the lock screen to the agent (the agent adds the device key and asks the server).</summary>
public static class PipeOps
{
    /// <summary>What a player can buy on this PC now (GET /api/devices/{id}/login-offers).</summary>
    public const string Offers = "offers";
    /// <summary>Player login and session start (POST /api/devices/{id}/player-login).</summary>
    public const string Login = "login";
    /// <summary>The player ends the session (POST /api/devices/{id}/player-logout).</summary>
    public const string Logout = "logout";
    /// <summary>The bar menu of this PC (GET /api/devices/{id}/bar-menu).</summary>
    public const string Menu = "menu";
    /// <summary>A bar order (POST /api/devices/{id}/orders).</summary>
    public const string Order = "order";
    /// <summary>The player cancels the new order (POST /api/devices/{id}/orders/cancel).</summary>
    public const string OrderCancel = "order_cancel";

    public static readonly IReadOnlyList<string> All = new[] { Offers, Login, Logout, Menu, Order, OrderCancel };
}

/// <summary>A line of a bar order (product and quantity).</summary>
public sealed record PipeOrderItem(Guid ProductId, int Quantity);

/// <summary>Items and Payment (balance or desk): a bar order.</summary>
public sealed record PipeRequest(string Op, string? Username = null, string? Secret = null, string? Offer = null, int? Minutes = null, Guid? PackageId = null,
    IReadOnlyList<PipeOrderItem>? Items = null, string? Payment = null);

/// <summary>Status: the HTTP status of the server, or 503 when the agent or the server cannot answer. Body: the JSON of the server.</summary>
public sealed record PipeResponse(int Status, string? Body)
{
    public bool Ok => Status is >= 200 and < 300;

    public static PipeResponse Error(int status, string code) => new(status, JsonSerializer.Serialize(new { code }));

    /// <summary>The "code" of an error body, if any.</summary>
    public string? Code => Field("code");

    /// <summary>The first of the "args" of an error body, if any.</summary>
    public string? FirstArg => Arg(0);

    /// <summary>One of the "args" of an error body, if any.</summary>
    public string? Arg(int index)
    {
        try
        {
            using var doc = JsonDocument.Parse(Body ?? "{}");
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("args", out var a) &&
                   a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > index ? a[index].ToString() : null;
        }
        catch (JsonException) { return null; }
    }

    private string? Field(string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(Body ?? "{}");
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public T? As<T>() where T : class
    {
        try { return Body == null ? null : JsonSerializer.Deserialize<T>(Body, AgentPipe.Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Local pipe between the lock screen (runs as the player) and the agent (a service): the lock screen
/// cannot read the device key, so the agent asks the server for it. One request per connection: a JSON
/// line each way, at most 16 KB, read within 5 s. On Windows the agent gives the players read and write
/// access only (they cannot create a pipe instance with this name while the agent runs), and the lock
/// screen checks that the pipe server is the agent process (the PID in state.json).
/// </summary>
public static class AgentPipe
{
    public const string Name = "ClubAgent.Launcher";
    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxConnections = 4;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads bytes up to a newline; null when the message is too long or the stream ends first.</summary>
    internal static async Task<string?> ReadLineAsync(Stream stream, int max, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1024];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return null;
            var newline = Array.IndexOf(one, (byte)'\n', 0, n);
            buffer.Write(one, 0, newline >= 0 ? newline : n);
            if (buffer.Length > max) return null;
            if (newline >= 0) return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    internal static async Task WriteLineAsync(Stream stream, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static PipeRequest? ParseRequest(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var req = JsonSerializer.Deserialize<PipeRequest>(line, Json);
            return req != null && PipeOps.All.Contains(req.Op) ? req : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The pipe server of the agent: answers each connection with the handler (at most 4 at a time).</summary>
public sealed class AgentPipeServer
{
    private readonly Func<bool, NamedPipeServerStream> _create;
    private readonly Func<PipeRequest, CancellationToken, Task<PipeResponse>> _handle;
    private readonly Action<Exception>? _log;
    private readonly SemaphoreSlim _slots = new(AgentPipe.MaxConnections);

    /// <param name="create">Creates a listening instance; the argument is true for the first one (FirstPipeInstance on Windows).</param>
    public AgentPipeServer(Func<bool, NamedPipeServerStream> create, Func<PipeRequest, CancellationToken, Task<PipeResponse>> handle, Action<Exception>? log = null)
    {
        _create = create;
        _handle = handle;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = _create(first);
                first = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke(ex); // another process holds the name, or the pipe limit is reached
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            catch (IOException ex)
            {
                _log?.Invoke(ex);
                await server.DisposeAsync();
                continue;
            }
            // A new instance listens at once (loop); this one is answered in the background
            _ = ServeAsync(server, ct);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        await using (stream)
        {
            if (!await _slots.WaitAsync(0, CancellationToken.None)) return; // too many at once: closed without an answer
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(AgentPipe.ReadTimeout);
                string? line;
                try { line = await AgentPipe.ReadLineAsync(stream, AgentPipe.MaxMessageBytes, timeout.Token); }
                catch (OperationCanceledException) { return; }
                var req = AgentPipe.ParseRequest(line);
                PipeResponse response;
                if (req == null) response = PipeResponse.Error(400, "bad_request");
                else
                {
                    try { response = await _handle(req, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log?.Invoke(ex);
                        response = PipeResponse.Error(500, "agent_error");
                    }
                }
                await AgentPipe.WriteLineAsync(stream, JsonSerializer.Serialize(response, AgentPipe.Json), ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // the lock screen went away
            }
            finally
            {
                _slots.Release();
            }
        }
    }
}

public static class AgentPipeClient
{
    /// <summary>
    /// Sends one request. 503 agent_unavailable when the agent does not answer in time; 503 agent_untrusted
    /// when verify refuses the pipe server (it is not the agent).
    /// </summary>
    public static async Task<PipeResponse> SendAsync(string pipeName, PipeRequest request, TimeSpan timeout,
        Func<NamedPipeClientStream, bool>? verify = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token);
            if (verify != null && !verify(client)) return PipeResponse.Error(503, "agent_untrusted");
            await AgentPipe.WriteLineAsync(client, JsonSerializer.Serialize(request, AgentPipe.Json), cts.Token);
            var line = await AgentPipe.ReadLineAsync(client, AgentPipe.MaxMessageBytes * 4, cts.Token);
            var response = line == null ? null : JsonSerializer.Deserialize<PipeResponse>(line, AgentPipe.Json);
            return response ?? PipeResponse.Error(503, "agent_unavailable");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or JsonException or UnauthorizedAccessException)
        {
            return PipeResponse.Error(503, "agent_unavailable");
        }
    }
}
