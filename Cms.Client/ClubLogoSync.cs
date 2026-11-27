using System.Net;
using Cms.Shared;

namespace Cms.Client;

/// <summary>
/// The agent keeps %ProgramData%\ClubAgent\club-logo.img equal to the club logo: the heartbeat brings the SHA-256 of the
/// logo; when it differs from the file, the agent downloads GET /api/public/theme/logo (HTTP 200 only, at most 512 KB),
/// checks the hash, writes a temporary file and moves it over the old one. Without a logo the file is removed. A failed
/// download keeps the old file, so the lock screen shows the last good logo or the club name text. The whole download
/// has a time limit: the agent calls this in its heartbeat loop, and a half-open connection must not stop that loop.
/// </summary>
public static class ClubLogoSync
{
    public enum Result { Unchanged, Downloaded, Removed, Failed }

    public const string LogoPath = "api/public/theme/logo";

    /// <summary>The longest a download may take (headers and body together).</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Downloads or removes the logo as the hash says. Failed (old file kept) also when the time limit runs out; a
    /// cancellation of <paramref name="ct"/> (the service stops) is thrown as usual.
    /// </summary>
    public static async Task<Result> SyncAsync(HttpClient http, string directory, string? sha256, CancellationToken ct, TimeSpan? timeLimit = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeLimit ?? DefaultTimeLimit);
        try
        {
            return await SyncCoreAsync(http, directory, sha256, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Failed;
        }
    }

    private static async Task<Result> SyncCoreAsync(HttpClient http, string directory, string? sha256, CancellationToken ct)
    {
        var path = Path.Combine(directory, AgentFiles.ClubLogoFile);
        if (sha256 == null)
        {
            if (!File.Exists(path)) return Result.Unchanged;
            File.Delete(path);
            return Result.Removed;
        }
        if (File.Exists(path) && LogoLoader.ReadTrusted(path, sha256) != null) return Result.Unchanged;

        using var res = await http.GetAsync(LogoPath, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode != HttpStatusCode.OK || res.Content.Headers.ContentLength > LogoLoader.MaxBytes) return Result.Failed;
        var buffer = new MemoryStream();
        await using (var body = await res.Content.ReadAsStreamAsync(ct))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > LogoLoader.MaxBytes) return Result.Failed;
                buffer.Write(chunk, 0, read);
            }
        }
        var bytes = LogoLoader.Check(buffer.ToArray(), sha256);
        if (bytes == null) return Result.Failed;

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return Result.Downloaded;
    }
}
