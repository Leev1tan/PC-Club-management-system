using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>A process list in memory: Kill with entireTree removes the children too.</summary>
internal sealed class FakeProcessTable : IProcessTable
{
    public List<ProcessInfo> Processes { get; } = new();
    public HashSet<int> Unkillable { get; } = new();
    public List<(int Pid, bool Tree)> Kills { get; } = new();
    public int CurrentSessionId { get; init; } = 2;
    public int CurrentPid { get; init; } = 1000;

    public IReadOnlyList<ProcessInfo> Snapshot() => Processes.ToList();

    public bool Kill(int pid, bool entireTree)
    {
        Kills.Add((pid, entireTree));
        if (Unkillable.Contains(pid) || Processes.All(p => p.Pid != pid)) return false;
        var gone = new HashSet<int> { pid };
        if (entireTree)
        {
            bool added;
            do
            {
                added = false;
                foreach (var p in Processes.Where(p => gone.Contains(p.ParentPid) && gone.Add(p.Pid))) added = true;
            } while (added);
        }
        Processes.RemoveAll(p => gone.Contains(p.Pid));
        return true;
    }
}

/// <summary>A Windows-like file system in memory (\\ separator, case-insensitive), with links.</summary>
internal sealed class FakeFileSystem : IFileSystem
{
    private sealed record Node(FileAttributes Attributes, bool Link);
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public char Separator => '\\';
    public bool CaseInsensitive => true;

    public FakeFileSystem Dir(string path, bool link = false)
    {
        _nodes[path] = new Node(FileAttributes.Directory | (link ? FileAttributes.ReparsePoint : 0), link);
        return this;
    }

    public FakeFileSystem File(string path, bool link = false, bool readOnly = false)
    {
        _nodes[path] = new Node((readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal) | (link ? FileAttributes.ReparsePoint : 0), link);
        return this;
    }

    public bool Exists(string path) => _nodes.ContainsKey(path);

    public FileAttributes? GetAttributes(string path) => _nodes.TryGetValue(path, out var n) ? n.Attributes : null;
    public bool IsLink(string path) => _nodes.TryGetValue(path, out var n) && n.Link;

    public IEnumerable<string> Entries(string directory) =>
        _nodes.Keys.Where(k => k.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase) && !k[(directory.Length + 1)..].Contains('\\')).ToList();

    public void DeleteFile(string path)
    {
        if (Locked.Contains(path)) throw new IOException("The file is in use");
        _nodes.Remove(path);
    }

    public void DeleteEmptyDirectory(string path)
    {
        if (Entries(path).Any()) throw new IOException("The directory is not empty");
        _nodes.Remove(path);
    }

    public string FullPath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 1) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('\\', parts);
    }
}

public sealed class ProcessTeardownTests
{
    private const int Player = 2, OtherUser = 3;

    [Fact]
    public void The_started_games_their_trees_and_the_configured_names_in_the_player_session_are_killed()
    {
        var table = new FakeProcessTable { CurrentPid = 1000, CurrentSessionId = Player };
        table.Processes.AddRange(new[]
        {
            new ProcessInfo(1000, "Cms.Launcher", Player),
            new ProcessInfo(900, "explorer", Player),
            new ProcessInfo(4, "System", 0),
            new ProcessInfo(510, "svchost", 0),
            new ProcessInfo(200, "steam", Player),
            new ProcessInfo(201, "steamwebhelper", Player, ParentPid: 200),
            new ProcessInfo(202, "cs2", Player, ParentPid: 200),
            new ProcessInfo(300, "MyGame", Player),              // started by the lock screen
            new ProcessInfo(301, "MyGameHelper", Player, ParentPid: 300),
            new ProcessInfo(400, "Discord", Player),
            new ProcessInfo(401, "chrome", OtherUser),           // another Windows session: never touched
            new ProcessInfo(402, "notepad", Player),             // neither started nor configured
            new ProcessInfo(403, "explorer", Player),            // configured by mistake, but protected
        });
        var started = new[] { new StartedProcess(300, "MyGame.exe"), new StartedProcess(777, "gone") };
        var plan = ProcessTeardown.Plan(table.Snapshot(), started, new[] { "steam", "Discord.exe", "steamwebhelper", "chrome", "explorer", "Cms.Launcher" }, Player, 1000);
        Assert.Equal(new[] { 200, 201, 300, 400 }, plan.Select(p => p.Pid));

        var (killed, failed) = ProcessTeardown.Kill(table, plan);
        Assert.Equal(new[] { "steam", "steamwebhelper", "MyGame", "Discord" }, killed);
        Assert.Empty(failed);
        Assert.All(table.Kills, k => Assert.True(k.Tree));
        Assert.DoesNotContain(table.Processes, p => p.Pid is 200 or 201 or 202 or 300 or 301 or 400); // cs2 and the helper went with their trees
        Assert.Contains(table.Processes, p => p.Pid == 401);
        Assert.Contains(table.Processes, p => p.Pid == 402);
        Assert.Contains(table.Processes, p => p.Pid == 1000);
    }

    [Fact]
    public void A_reused_process_id_is_not_killed_and_a_failure_is_reported()
    {
        var table = new FakeProcessTable();
        table.Processes.Add(new ProcessInfo(300, "WinWord", Player)); // the started game was pid 300, now Word has it
        table.Processes.Add(new ProcessInfo(500, "firefox", Player));
        table.Unkillable.Add(500);
        var plan = ProcessTeardown.Plan(table.Snapshot(), new[] { new StartedProcess(300, "MyGame") }, new[] { "firefox" }, Player, 1000);
        Assert.Equal(new[] { 500 }, plan.Select(p => p.Pid));
        var (killed, failed) = ProcessTeardown.Kill(table, plan);
        Assert.Empty(killed);
        Assert.Equal(new[] { "firefox" }, failed);
    }

    [Fact]
    public void The_default_settings_kill_the_launchers_and_browsers_and_never_clean_up()
    {
        var settings = TeardownSettings.Default;
        foreach (var name in new[] { "steam", "EpicGamesLauncher", "Discord", "chrome", "msedge", "firefox" })
            Assert.Contains(name, settings.KillProcessNames);
        Assert.False(settings.Cleanup.Enabled);
        Assert.Empty(settings.Cleanup.Paths);
        var back = TeardownSettings.Parse(new TeardownSettings
        {
            KillProcessNames = new[] { "steam" },
            Cleanup = new CleanupSettings { Enabled = true, AllowedRoots = new[] { "%APPDATA%" }, Paths = new[] { @"%APPDATA%\discord\Local Storage" } }
        }.ToJson())!;
        Assert.Equal(new[] { "steam" }, back.KillProcessNames);
        Assert.True(back.Cleanup.Enabled);
        Assert.Null(TeardownSettings.Parse("{broken"));
    }
}

public sealed class DataCleanupTests
{
    private const string Local = @"C:\Users\club\AppData\Local";
    private const string Roaming = @"C:\Users\club\AppData\Roaming";

    private static string Expand(string s) => s.Replace("%LOCALAPPDATA%", Local).Replace("%APPDATA%", Roaming);

    private static FakeFileSystem Profile() => new FakeFileSystem()
        .Dir(@"C:\Users").Dir(@"C:\Users\club").Dir(@"C:\Users\club\AppData").Dir(Local).Dir(Roaming)
        .Dir(Local + @"\Google").Dir(Local + @"\Google\Chrome").Dir(Local + @"\Google\Chrome\User Data")
        .Dir(Local + @"\Google\Chrome\User Data\Default").File(Local + @"\Google\Chrome\User Data\Default\Cookies")
        .File(Local + @"\Google\Chrome\User Data\Default\Login Data", readOnly: true)
        .File(Local + @"\Google\Chrome\User Data\Local State")
        .Dir(Roaming + @"\discord").Dir(Roaming + @"\discord\Local Storage").File(Roaming + @"\discord\Local Storage\leveldb.log")
        .Dir(@"C:\Windows").Dir(@"C:\Windows\System32").File(@"C:\Windows\System32\kernel32.dll");

    private static CleanupSettings On(params string[] paths) => new() { Enabled = true, AllowedRoots = new[] { "%LOCALAPPDATA%", "%APPDATA%" }, Paths = paths };

    [Fact]
    public void Off_by_default_nothing_is_touched()
    {
        var fs = Profile();
        var report = DataCleanup.Run(fs, new CleanupSettings { Paths = new[] { @"%LOCALAPPDATA%\Google" } }, Expand);
        Assert.Same(CleanupReport.None, report);
        Assert.True(fs.Exists(Local + @"\Google\Chrome\User Data\Default\Cookies"));
    }

    [Fact]
    public void Configured_paths_inside_the_roots_are_removed_and_reported()
    {
        var fs = Profile();
        var report = DataCleanup.Run(fs, On(@"%LOCALAPPDATA%\Google\Chrome\User Data", @"%APPDATA%\discord\Local Storage\leveldb.log", @"%APPDATA%\missing"), Expand);
        Assert.Equal((4, 2), (report.RemovedFiles, report.RemovedDirectories)); // Cookies, Login Data (read-only), Local State, leveldb.log; Default, User Data
        Assert.Equal(new[] { Local + @"\Google\Chrome\User Data", Roaming + @"\discord\Local Storage\leveldb.log" }, report.RemovedPaths);
        Assert.False(fs.Exists(Local + @"\Google\Chrome\User Data"));
        Assert.True(fs.Exists(Local + @"\Google\Chrome"));
        Assert.True(fs.Exists(Roaming + @"\discord\Local Storage"));
        Assert.Empty(report.Errors);
        Assert.Empty(report.Skipped); // a missing path is not an error
    }

    [Theory]
    [InlineData(@"C:\Windows\System32", "outside the allowed roots")]
    [InlineData(@"%LOCALAPPDATA%\..\..\..\..\Windows\System32", "outside the allowed roots")]
    [InlineData(@"%LOCALAPPDATA%Low\Temp", "outside the allowed roots")]
    [InlineData(@"%LOCALAPPDATA%", "an allowed root itself is never deleted")]
    [InlineData(@"%USERPROFILE%\Documents", "not an absolute path")]
    [InlineData(@"Google\Chrome", "not an absolute path")]
    [InlineData(@"\\server\share\x", "not an absolute path")]
    public void Paths_outside_the_roots_are_never_deleted(string path, string why)
    {
        var fs = Profile();
        var report = DataCleanup.Run(fs, On(path), Expand);
        Assert.Equal((0, 0), (report.RemovedFiles, report.RemovedDirectories));
        Assert.Contains(report.Skipped, s => s == $"{path}: {why}");
        Assert.True(fs.Exists(@"C:\Windows\System32\kernel32.dll"));
        Assert.True(fs.Exists(Local));
    }

    [Fact]
    public void Links_are_never_followed_as_the_target_in_the_path_or_inside()
    {
        // A player planted junctions: one as the target, one in the path, one inside the cleaned tree
        var fs = Profile()
            .Dir(Local + @"\Evil", link: true).File(Local + @"\Evil\kernel32.dll")
            .Dir(Local + @"\Steam", link: true).Dir(Local + @"\Steam\htmlcache").File(Local + @"\Steam\htmlcache\a")
            .Dir(Local + @"\Google\Chrome\User Data\Default\Jump", link: true).File(Local + @"\Google\Chrome\User Data\Default\Jump\important.doc")
            .File(Roaming + @"\discord\Local Storage\link.log", link: true);
        var report = DataCleanup.Run(fs, On(@"%LOCALAPPDATA%\Evil", @"%LOCALAPPDATA%\Steam\htmlcache", @"%LOCALAPPDATA%\Google\Chrome\User Data",
            @"%APPDATA%\discord\Local Storage\link.log"), Expand);

        Assert.Contains(@"%LOCALAPPDATA%\Evil: a link, not followed", report.Skipped);
        Assert.Contains(@"%LOCALAPPDATA%\Steam\htmlcache: a link in the path, not followed", report.Skipped);
        Assert.Contains(Local + @"\Google\Chrome\User Data\Default\Jump: a link, not followed", report.Skipped);
        Assert.Contains(@"%APPDATA%\discord\Local Storage\link.log: a link, not followed", report.Skipped);
        Assert.True(fs.Exists(Local + @"\Evil\kernel32.dll"));
        Assert.True(fs.Exists(Local + @"\Steam\htmlcache\a"));
        Assert.True(fs.Exists(Local + @"\Google\Chrome\User Data\Default\Jump\important.doc"));
        Assert.True(fs.Exists(Roaming + @"\discord\Local Storage\link.log"));
        // The rest of the Chrome tree is gone; the directories that hold a link stay
        Assert.False(fs.Exists(Local + @"\Google\Chrome\User Data\Default\Cookies"));
        Assert.True(fs.Exists(Local + @"\Google\Chrome\User Data\Default"));
    }

    [Fact]
    public void A_root_that_is_a_link_or_a_drive_is_refused_and_files_in_use_are_errors()
    {
        var fs = Profile().Dir(@"D:\Linked", link: true).File(@"D:\Linked\x");
        fs.Locked.Add(Local + @"\Google\Chrome\User Data\Local State");
        var report = DataCleanup.Run(fs, new CleanupSettings
        {
            Enabled = true,
            AllowedRoots = new[] { "%LOCALAPPDATA%", @"D:\Linked", @"C:\", "%NOT_SET%" },
            Paths = new[] { @"D:\Linked\x", @"%LOCALAPPDATA%\Google\Chrome\User Data" }
        }, Expand);
        Assert.Contains(@"root D:\Linked: a link, not followed", report.Skipped);
        Assert.Contains(@"root C:\: a drive or the file system root is never allowed", report.Skipped);
        Assert.Contains("root %NOT_SET%: not an absolute path", report.Skipped);
        Assert.True(fs.Exists(@"D:\Linked\x"));
        Assert.Contains(report.Errors, e => e.EndsWith("Local State: The file is in use"));
        Assert.True(fs.Exists(Local + @"\Google\Chrome\User Data")); // not empty: stays
        Assert.Equal(2, report.RemovedFiles); // Cookies and Login Data; Local State is in use
    }
}

/// <summary>The real file system with real symbolic links (Linux in CI; the same rules on Windows).</summary>
public sealed class SystemFileSystemCleanupTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ccms-cleanup-" + Guid.NewGuid().ToString("N"), "home");

    public SystemFileSystemCleanupTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_base)!, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void Real_links_are_not_followed_and_their_targets_survive()
    {
        if (OperatingSystem.IsWindows()) return; // symbolic links need extra rights on Windows; the fake file system covers it there
        var root = Path.Combine(_base, "profile");
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(root, "cache", "deep"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "cache", "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "cache", "deep", "b.txt"), "b");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(root, "cache", "deep", "escape"), outside);
        File.CreateSymbolicLink(Path.Combine(root, "cache", "file-link"), Path.Combine(outside, "keep.txt"));
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

        var report = DataCleanup.Run(new SystemFileSystem(), new CleanupSettings
        {
            Enabled = true,
            AllowedRoots = new[] { root },
            Paths = new[] { Path.Combine(root, "cache"), Path.Combine(root, "linked"), Path.Combine(root, "linked", "keep.txt"), Path.Combine(root, "..", "outside") }
        }, s => s);

        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(root, "cache", "a.txt")));
        Assert.False(File.Exists(Path.Combine(root, "cache", "deep", "b.txt")));
        Assert.Equal(2, report.RemovedFiles);
        Assert.Contains(report.Skipped, s => s.EndsWith("escape: a link, not followed"));
        Assert.Contains(report.Skipped, s => s.EndsWith("file-link: a link, not followed"));
        Assert.Contains(report.Skipped, s => s.EndsWith("linked: a link, not followed"));
        Assert.Contains(report.Skipped, s => s.EndsWith("keep.txt: a link in the path, not followed"));
        Assert.Contains(report.Skipped, s => s.EndsWith("outside: outside the allowed roots"));
        Assert.True(Directory.Exists(Path.Combine(root, "cache", "deep"))); // keeps the link
    }
}

public sealed class TeardownTriggerTests
{
    private static readonly Guid S = Guid.NewGuid();

    [Fact]
    public void A_pause_or_a_staff_lock_during_the_session_is_no_teardown_the_end_is()
    {
        var t = new TeardownTrigger();
        Assert.False(t.Update(locked: true, agentAlive: true, "manual", null)); // locked at start: nothing was played
        Assert.False(t.Update(false, true, "none", S));
        Assert.False(t.Update(true, true, "manual", S));                       // paused or locked by staff: S still runs
        Assert.False(t.Update(false, true, "none", S));
        Assert.True(t.Update(true, true, "manual", null));                     // the server ended S
        Assert.False(t.Update(true, true, "manual", null));                    // once
    }

    [Fact]
    public void A_silent_agent_never_starts_a_teardown()
    {
        var t = new TeardownTrigger();
        t.Update(false, true, "none", S);
        Assert.False(t.Update(true, agentAlive: false, null, null)); // the agent stopped: the session may still run
        Assert.True(t.Update(true, true, "manual", null));           // back: the session is over
    }

    [Fact]
    public void An_expired_session_a_moved_session_and_an_unlock_without_a_session_end_with_a_teardown()
    {
        var expired = new TeardownTrigger();
        expired.Update(false, true, "none", S);
        Assert.True(expired.Update(true, true, "session_expired", S)); // the agent ended it offline; the server does not know yet

        var moved = new TeardownTrigger();
        moved.Update(false, true, "none", S);
        Assert.True(moved.Update(true, true, "manual", Guid.NewGuid()));

        var free = new TeardownTrigger();
        free.Update(false, true, "none", null); // unlocked by staff without a session
        Assert.True(free.Update(true, true, "manual", null));
    }

    [Fact]
    public void The_trigger_says_whether_a_session_ended_or_a_staff_unlock_without_a_session()
    {
        // Windows re-test 2026-11-27 late, LOW 9: agent.log said "the ended session" also after a staff PIN unlock
        var t = new TeardownTrigger();
        t.Update(false, true, "none", null);              // staff unlock, no session
        Assert.True(t.Update(true, true, "manual", null));
        Assert.True(t.LastWithoutSession);
        t.Update(false, true, "none", S);                 // then a session
        Assert.True(t.Update(true, true, "manual", null));
        Assert.False(t.LastWithoutSession);
    }
}
