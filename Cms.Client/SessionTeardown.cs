using System.Text.Json;

namespace Cms.Client;

/// <summary>
/// What the lock screen does when a session ends (teardown.json, written by the agent from the section
/// "Teardown" of its appsettings.json on the PC; the server never sends it, so nobody can order
/// deletions over the network):
///  - KillProcessNames: process names (without .exe) killed with their child processes in the player's
///    Windows session, in addition to every process that the lock screen started;
///  - Cleanup: OFF by default. When on, the Paths are deleted, but only inside the AllowedRoots, never
///    through a symbolic link, junction or other reparse point, and every removed path is reported.
/// %VARIABLES% are expanded in the player's session (the lock screen), not in the agent's.
/// </summary>
public sealed class TeardownSettings
{
    public static readonly IReadOnlyList<string> DefaultKillProcessNames = new[]
    {
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices", "RiotClientUx",
        "EADesktop", "Origin", "GalaxyClient", "upc", "Discord", "Telegram", "chrome", "msedge", "firefox", "opera", "brave",
    };

    public static readonly IReadOnlyList<string> DefaultAllowedRoots = new[] { "%LOCALAPPDATA%", "%APPDATA%" };

    public IReadOnlyList<string> KillProcessNames { get; init; } = DefaultKillProcessNames;
    public CleanupSettings Cleanup { get; init; } = new();

    public static readonly TeardownSettings Default = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TeardownSettings? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TeardownSettings>(json, Json); }
        catch (JsonException) { return null; }
    }
}

public sealed class CleanupSettings
{
    public bool Enabled { get; init; }
    public IReadOnlyList<string> AllowedRoots { get; init; } = TeardownSettings.DefaultAllowedRoots;
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
}

/// <summary>A process the lock screen started (the name guards against a reused process id).</summary>
public sealed record StartedProcess(int Pid, string Name);

/// <summary>What the teardown did, for the agent inbox (audit log) and the local log.</summary>
public sealed record TeardownReport(IReadOnlyList<string> Killed, IReadOnlyList<string> KillFailed, CleanupReport Cleanup);

public static class ProcessTeardown
{
    /// <summary>Never killed, whatever the configuration says (the shell and Windows itself).</summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "winlogon", "csrss", "dwm", "lsass", "services", "smss", "wininit", "svchost", "sihost", "ctfmon", "fontdrvhost",
        "conhost", "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "TextInputHost", "LogonUI", "userinit", "dllhost", "System", "Idle", "Registry", "Cms.Launcher", "Cms.Agent.Service",
    };

    private static string Plain(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// The processes to kill: in the player's session only, never this program or a protected name; the
    /// processes the lock screen started (same id and name) and every process with a configured name.
    /// </summary>
    public static IReadOnlyList<ProcessInfo> Plan(IReadOnlyList<ProcessInfo> processes, IEnumerable<StartedProcess> started,
        IEnumerable<string> killNames, int sessionId, int selfPid)
    {
        var names = new HashSet<string>(killNames.Select(Plain).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        var startedSet = started.ToList();
        return processes
            .Where(p => p.SessionId == sessionId && p.Pid != selfPid && p.Pid > 4 && !Protected.Contains(p.Name))
            .Where(p => names.Contains(p.Name) ||
                        startedSet.Any(s => s.Pid == p.Pid && string.Equals(Plain(s.Name), p.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Pid)
            .ToList();
    }

    /// <summary>Kills the planned processes with their child processes. Returns the names killed and those that could not be killed.</summary>
    public static (IReadOnlyList<string> Killed, IReadOnlyList<string> Failed) Kill(IProcessTable table, IReadOnlyList<ProcessInfo> plan)
    {
        var killed = new List<string>();
        var failed = new List<string>();
        foreach (var p in plan)
        {
            // A child of a process killed before is gone already: no error
            if (!table.Snapshot().Any(x => x.Pid == p.Pid)) { killed.Add(p.Name); continue; }
            (table.Kill(p.Pid, entireTree: true) ? killed : failed).Add(p.Name);
        }
        return (killed, failed);
    }
}

/// <summary>The file system, behind an interface for the tests (fake tree) and the real one (SystemFileSystem).</summary>
public interface IFileSystem
{
    /// <summary>Attributes of the entry itself (a link is not followed); null when it does not exist.</summary>
    FileAttributes? GetAttributes(string path);
    /// <summary>True for a symbolic link, junction or other reparse point (also when GetAttributes misses it).</summary>
    bool IsLink(string path);
    /// <summary>Full paths of the entries of a directory (not recursive, links are not followed).</summary>
    IEnumerable<string> Entries(string directory);
    void DeleteFile(string path);
    void DeleteEmptyDirectory(string path);
    /// <summary>Normalized absolute path ("..", "." and double separators resolved; no access to the disk).</summary>
    string FullPath(string path);
    char Separator { get; }
    bool CaseInsensitive { get; }
}

public sealed class SystemFileSystem : IFileSystem
{
    public char Separator => Path.DirectorySeparatorChar;
    public bool CaseInsensitive => OperatingSystem.IsWindows();

    public FileAttributes? GetAttributes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists || info.LinkTarget != null) return info.Attributes;
            var dir = new DirectoryInfo(path);
            return dir.Exists || dir.LinkTarget != null ? dir.Attributes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // unknown: treat as a link (never followed)
        }
    }

    public IEnumerable<string> Entries(string directory) => Directory.EnumerateFileSystemEntries(directory);

    public void DeleteFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public void DeleteEmptyDirectory(string path) => Directory.Delete(path, recursive: false);

    public string FullPath(string path) => Path.GetFullPath(path);
}

/// <summary>Result of a cleanup: removed files and directories, what was skipped and why, and errors.</summary>
public sealed record CleanupReport(int RemovedFiles, int RemovedDirectories, IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors)
{
    public static readonly CleanupReport None = new(0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>Deletes the configured per-user data paths, only inside the allowed roots, never through a link.</summary>
public static class DataCleanup
{
    public static CleanupReport Run(IFileSystem fs, CleanupSettings settings, Func<string, string> expand)
    {
        if (!settings.Enabled || settings.Paths.Count == 0) return CleanupReport.None;
        var skipped = new List<string>();
        var errors = new List<string>();
        var removed = new List<string>();
        var counts = new int[2];
        var comparison = fs.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var roots = new List<string>();
        foreach (var raw in settings.AllowedRoots)
        {
            var root = Normalize(fs, expand(raw));
            if (root == null) { skipped.Add($"root {raw}: not an absolute path"); continue; }
            if (IsTopLevel(fs, root)) { skipped.Add($"root {raw}: a drive or the file system root is never allowed"); continue; }
            var attributes = fs.GetAttributes(root);
            if (attributes == null) { skipped.Add($"root {raw}: missing"); continue; }
            if (attributes.Value.HasFlag(FileAttributes.ReparsePoint) || fs.IsLink(root)) { skipped.Add($"root {raw}: a link, not followed"); continue; }
            roots.Add(root);
        }

        foreach (var raw in settings.Paths)
        {
            var target = Normalize(fs, expand(raw));
            if (target == null) { skipped.Add($"{raw}: not an absolute path"); continue; }
            if (roots.Any(r => string.Equals(r, target, comparison))) { skipped.Add($"{raw}: an allowed root itself is never deleted"); continue; }
            var root = roots.FirstOrDefault(r => target.StartsWith(r + fs.Separator, comparison));
            if (root == null) { skipped.Add($"{raw}: outside the allowed roots"); continue; }

            // Every directory between the root and the target must be a real directory, not a link
            var relative = target[(root.Length + 1)..].Split(fs.Separator, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var blocked = false;
            for (var i = 0; i < relative.Length; i++)
            {
                current = current + fs.Separator + relative[i];
                var attributes = fs.GetAttributes(current);
                if (attributes == null) { blocked = true; break; } // nothing to delete
                if (attributes.Value.HasFlag(FileAttributes.ReparsePoint) || fs.IsLink(current))
                {
                    skipped.Add(i == relative.Length - 1 ? $"{raw}: a link, not followed" : $"{raw}: a link in the path, not followed");
                    blocked = true;
                    break;
                }
            }
            if (blocked) continue;

            var before = (counts[0], counts[1]);
            if (fs.GetAttributes(target)!.Value.HasFlag(FileAttributes.Directory)) DeleteTree(fs, target, counts, skipped, errors);
            else TryDelete(() => fs.DeleteFile(target), target, errors, () => counts[0]++);
            if ((counts[0], counts[1]) != before) removed.Add(target);
        }
        return new CleanupReport(counts[0], counts[1], removed, skipped, errors);
    }

    /// <summary>Deletes the files and directories of a directory tree; links inside are skipped (not followed, not deleted).</summary>
    private static void DeleteTree(IFileSystem fs, string directory, int[] counts, List<string> skipped, List<string> errors)
    {
        List<string> entries;
        try { entries = fs.Entries(directory).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{directory}: {ex.Message}"); return; }
        foreach (var entry in entries)
        {
            var attributes = fs.GetAttributes(entry);
            if (attributes == null) continue;
            if (attributes.Value.HasFlag(FileAttributes.ReparsePoint) || fs.IsLink(entry)) { skipped.Add($"{entry}: a link, not followed"); continue; }
            if (attributes.Value.HasFlag(FileAttributes.Directory)) DeleteTree(fs, entry, counts, skipped, errors);
            else TryDelete(() => fs.DeleteFile(entry), entry, errors, () => counts[0]++);
        }
        // A directory that still has a skipped link or a file in use stays
        TryDelete(() => fs.DeleteEmptyDirectory(directory), directory, errors, () => counts[1]++, quiet: true);
    }

    private static void TryDelete(Action delete, string path, List<string> errors, Action counted, bool quiet = false)
    {
        try
        {
            delete();
            counted();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!quiet) errors.Add($"{path}: {ex.Message}");
        }
    }

    private static string? Normalize(IFileSystem fs, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('%') || path.Contains('\0')) return null;
        var rooted = fs.Separator == '\\'
            ? path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            : path.StartsWith('/');
        if (!rooted) return null;
        try
        {
            return fs.FullPath(path).TrimEnd(fs.Separator);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsTopLevel(IFileSystem fs, string root) =>
        root.Length == 0 || (fs.Separator == '\\' && root.Length <= 2) || root.Split(fs.Separator, StringSplitOptions.RemoveEmptyEntries).Length < 2;
}

/// <summary>
/// Decides when the lock screen runs the teardown: the PC was unlocked (with a session, or without one
/// after a staff unlock) and the agent now locks it because that session is over: it ended, expired, moved
/// to another PC, or there was none. Not on a pause or a staff lock during the session, and never while
/// the agent is silent (the session may still run on the server). Once per unlock.
/// </summary>
public sealed class TeardownTrigger
{
    private Guid? _seen; // the session seen while unlocked; Guid.Empty = unlocked without a session

    /// <summary>The last teardown of Update followed an unlock without a session (a staff unlock): no session ended.</summary>
    public bool LastWithoutSession { get; private set; }

    /// <summary>Call on every state read. Returns true when the teardown must run now.</summary>
    public bool Update(bool locked, bool agentAlive, string? agentLockReason, Guid? activeSessionId)
    {
        if (!agentAlive) return false;
        if (!locked)
        {
            _seen = activeSessionId ?? Guid.Empty;
            return false;
        }
        if (_seen is not { } seen) return false;
        if (seen != Guid.Empty && activeSessionId == seen && agentLockReason != "session_expired") return false;
        _seen = null;
        LastWithoutSession = seen == Guid.Empty;
        return true;
    }
}
