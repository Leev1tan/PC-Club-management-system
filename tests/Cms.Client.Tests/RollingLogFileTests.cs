using Cms.Client;
using Xunit;

namespace Cms.Client.Tests;

/// <summary>Windows test 2026-11-27, defect 6: the agent had no log. Its text log rolls over and keeps a few MB at most.</summary>
public sealed class RollingLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccms-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_log_rolls_over_and_keeps_at_most_the_given_number_of_files()
    {
        var log = new RollingLogFile(_dir, maxBytes: 2048, keepFiles: 3);
        var line = new string('x', 300);
        for (var i = 0; i < 100; i++) Assert.True(log.Write($"{i:D3} {line}"));

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "agent.1.log", "agent.2.log", "agent.log" }, files);
        Assert.All(Directory.GetFiles(_dir), f => Assert.True(new FileInfo(f).Length <= 2048, f));
        Assert.Contains("099 ", File.ReadAllText(log.CurrentPath));            // the newest entry is in agent.log
        Assert.DoesNotContain("000 ", File.ReadAllText(log.OldPath(2)));        // the oldest entries are gone
        var one = File.ReadAllLines(log.OldPath(1)).Select(l => int.Parse(l[..3])).ToList();
        var current = File.ReadAllLines(log.CurrentPath).Select(l => int.Parse(l[..3])).ToList();
        Assert.True(one.Max() < current.Min());                                   // agent.1.log is older than agent.log
    }

    [Fact]
    public void The_default_keeps_four_files_of_one_megabyte()
    {
        Assert.Equal(1024 * 1024, RollingLogFile.DefaultMaxBytes);
        Assert.Equal(4, RollingLogFile.DefaultKeepFiles);
        var log = new RollingLogFile(_dir);
        Assert.EndsWith(Path.Combine(Path.GetFileName(_dir), "agent.log"), log.CurrentPath);
        Assert.EndsWith("agent.3.log", log.OldPath(3));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_never_throws()
    {
        File.WriteAllText(_dir, "a file, not a folder");
        try
        {
            Assert.False(new RollingLogFile(_dir).Write("line"));
        }
        finally
        {
            File.Delete(_dir);
        }
    }

    [Fact]
    public void An_entry_is_one_line_and_a_value_cannot_fake_another_line()
    {
        var at = new DateTimeOffset(2026, 11, 27, 15, 10, 0, 123, TimeSpan.Zero);
        var text = RollingLogFile.Format(at, "WARN", "Cms.Agent.Service.Worker", "Server connection failed: bad\r\n2026-11-27T00:00:00.000Z INFO  fake");
        Assert.Single(text.Split(Environment.NewLine));
        Assert.Equal("2026-11-27T15:10:00.123Z WARN  Worker: Server connection failed: bad  2026-11-27T00:00:00.000Z INFO  fake", text);
    }

    [Fact]
    public void An_exception_follows_on_indented_lines_with_its_inner_exception()
    {
        Exception error;
        try { throw new HttpRequestException("refused", new System.Net.Sockets.SocketException(10061)); }
        catch (Exception ex) { error = ex; }
        var lines = RollingLogFile.Format(DateTimeOffset.UnixEpoch, "ERROR", "Microsoft.Hosting.Lifetime", "loop failed", error).Split(Environment.NewLine);
        Assert.StartsWith("1970-01-01T00:00:00.000Z ERROR Microsoft.Hosting.Lifetime: loop failed", lines[0]);
        Assert.Equal("    System.Net.Http.HttpRequestException: refused", lines[1]);
        Assert.StartsWith("      at ", lines[2]);
        Assert.Contains(lines, l => l.StartsWith("    inner: System.Net.Sockets.SocketException: ", StringComparison.Ordinal));
    }
}
