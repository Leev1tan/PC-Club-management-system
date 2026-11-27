namespace Cms.Client;

/// <summary>The result of the check of a pipe client (the lock screen) by the agent.</summary>
public enum PipeClientVerdict
{
    /// <summary>The installed lock screen in the interactive session: trusted.</summary>
    Trusted,
    /// <summary>The program of the client could not be read (it went away, or no access): refused (fail closed).</summary>
    NoProgram,
    /// <summary>Another program (for example a copy of the lock screen in the player's folder): refused.</summary>
    OtherProgram,
    /// <summary>The client runs in session 0 (services) or its session is unknown: refused.</summary>
    ServiceSession,
    /// <summary>The client is not in the session of the console (the player at the PC): refused.</summary>
    NotConsoleSession
}

/// <summary>
/// Which pipe client gets club account credentials (and may report the lock screen state): only the installed lock
/// screen, AccountPool:LauncherPath, in the session of the player at the console. The agent (LocalService) reads the
/// program of the client while it impersonates the client (Windows test 2026-11-27, defect 3: Process.MainModule of a
/// ClubUser process is empty for LocalService). This part has no Windows APIs, so it is tested on any OS.
/// </summary>
public static class PipeClientCheck
{
    /// <param name="clientImagePath">The full path of the program of the client (QueryFullProcessImageName), or null.</param>
    /// <param name="launcherPath">AccountPool:LauncherPath (default C:\ClubLauncher\Cms.Launcher.exe).</param>
    /// <param name="clientSessionId">The Windows session of the client (GetNamedPipeClientSessionId), or null.</param>
    /// <param name="consoleSessionId">The session at the console (WTSGetActiveConsoleSessionId; 0xFFFFFFFF = none).</param>
    /// <param name="consoleOnly">AccountPool:ConsoleSessionOnly (default true): the client must be in the console session.</param>
    public static PipeClientVerdict Decide(string? clientImagePath, string? launcherPath, uint? clientSessionId, uint? consoleSessionId, bool consoleOnly)
    {
        if (LauncherPath.Normalize(clientImagePath) == null) return PipeClientVerdict.NoProgram;
        if (!LauncherPath.Same(clientImagePath, launcherPath)) return PipeClientVerdict.OtherProgram;
        if (clientSessionId is null or 0) return PipeClientVerdict.ServiceSession;
        if (consoleOnly && clientSessionId != consoleSessionId) return PipeClientVerdict.NotConsoleSession;
        return PipeClientVerdict.Trusted;
    }
}

/// <summary>Comparison of Windows program paths (the lock screen against AccountPool:LauncherPath).</summary>
public static class LauncherPath
{
    private static readonly char[] Forbidden = { '"', '<', '>', '|', '*', '?' };

    /// <summary>
    /// The full local path in one form ("C:\Dir\File.exe"): / is \, a \\?\ or \??\ prefix is removed, empty and "."
    /// parts are removed and ".." goes one folder up. Null for an empty, relative, network (UNC) or invalid path, and for
    /// ".." above the drive.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') return null; // also \\server\share
        if (p.Any(char.IsControl) || p.IndexOfAny(Forbidden) >= 0 || p.IndexOf(':', 2) >= 0) return null;
        var parts = new List<string>();
        foreach (var part in p[3..].Split('\\'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        if (parts.Count == 0) return null;
        return char.ToUpperInvariant(p[0]) + @":\" + string.Join('\\', parts);
    }

    /// <summary>True when both are valid full local paths of the same file (Windows paths: case-insensitive).</summary>
    public static bool Same(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        return x != null && y != null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
