using System.Globalization;
using System.Text;

namespace Cms.Client;

/// <summary>
/// The text log of the agent (Windows test 2026-11-27, defect 6: the agent had no log). %ProgramData%\ClubAgent\logs\
/// agent.log; when it reaches MaxBytes it becomes agent.1.log (agent.1 becomes agent.2 ...), and at most KeepFiles files
/// stay (4 x 1 MB by default). The folder is readable by Administrators only (the agent and the installer set its ACL).
/// One entry per line: control characters and line breaks of a message are replaced, so a value cannot fake a line.
/// The callers never log tokens, keys, PINs, passwords or account secrets. Thread-safe; a write error is ignored.
/// </summary>
public sealed class RollingLogFile
{
    public const string BaseName = "agent";
    public const long DefaultMaxBytes = 1024 * 1024;
    public const int DefaultKeepFiles = 4;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _keepFiles;

    public RollingLogFile(string directory, long maxBytes = DefaultMaxBytes, int keepFiles = DefaultKeepFiles)
    {
        _directory = directory;
        _maxBytes = Math.Max(1024, maxBytes);
        _keepFiles = Math.Max(1, keepFiles);
    }

    public string CurrentPath => Path.Combine(_directory, BaseName + ".log");

    /// <summary>The path of an older file: 1 = agent.1.log (the newest old one).</summary>
    public string OldPath(int index) => Path.Combine(_directory, $"{BaseName}.{index}.log");

    /// <summary>Writes one entry (one or more lines, UTF-8). False when the file could not be written.</summary>
    public bool Write(string entry)
    {
        var bytes = Encoding.UTF8.GetBytes(entry.EndsWith('\n') ? entry : entry + Environment.NewLine);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var current = new FileInfo(CurrentPath);
                if (current.Exists && current.Length > 0 && current.Length + bytes.Length > _maxBytes) Rotate();
                using var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // never stop the agent because of its log
            }
        }
    }

    private void Rotate()
    {
        var oldest = OldPath(_keepFiles - 1);
        if (_keepFiles == 1)
        {
            File.Delete(CurrentPath);
            return;
        }
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = _keepFiles - 2; i >= 1; i--)
        {
            var from = OldPath(i);
            if (File.Exists(from)) File.Move(from, OldPath(i + 1));
        }
        File.Move(CurrentPath, OldPath(1));
    }

    /// <summary>
    /// One log entry: "2026-11-27T15:10:00.123Z WARN  Category: message", then the exception (type, message, stack trace)
    /// on indented lines. Control characters and line breaks of the message become spaces.
    /// </summary>
    public static string Format(DateTimeOffset timeUtc, string level, string category, string message, Exception? exception = null)
    {
        var sb = new StringBuilder();
        sb.Append(timeUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
          .Append(' ').Append(level.PadRight(5)).Append(' ')
          .Append(OneLine(ShortCategory(category))).Append(": ").Append(OneLine(message));
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            sb.Append(Environment.NewLine).Append("    ").Append(ex == exception ? "" : "inner: ").Append(OneLine(ex.GetType().FullName ?? "?"))
              .Append(": ").Append(OneLine(ex.Message));
            if (ex.StackTrace is { } stack)
                foreach (var line in stack.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    sb.Append(Environment.NewLine).Append("      ").Append(OneLine(line.Trim()));
        }
        return sb.ToString();
    }

    /// <summary>The text without control characters (a line break, a tab and others become a space).</summary>
    public static string OneLine(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    /// <summary>"Cms.Agent.Service.Worker" becomes "Worker"; other categories stay.</summary>
    private static string ShortCategory(string category) =>
        category.StartsWith("Cms.Agent.Service.", StringComparison.Ordinal) ? category["Cms.Agent.Service.".Length..] : category;
}
