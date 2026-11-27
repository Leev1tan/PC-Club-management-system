using System.Security.AccessControl;
using System.Security.Principal;
using Cms.Client;
using Cms.Shared;

namespace Cms.Agent.Service;

/// <summary>
/// The text log of the agent: %ProgramData%\ClubAgent\logs\agent.log, 4 files of 1 MB at most (Cms.Client.RollingLogFile).
/// Windows test 2026-11-27, defect 6: the agent had no log (the Application log dropped Information, and LocalService could
/// not create its event source). Levels follow "Logging" in appsettings.json (default Information; HttpClient and
/// Microsoft.* only from Warning, see Program.cs).
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly RollingLogFile _file;

    public FileLoggerProvider(RollingLogFile file) => _file = file;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_file, categoryName);

    public void Dispose()
    {
    }

    private sealed class FileLogger(RollingLogFile file, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            try { message = formatter(state, exception); }
            catch (FormatException) { message = state?.ToString() ?? string.Empty; }
            file.Write(RollingLogFile.Format(DateTimeOffset.UtcNow, Level(logLevel), category, message, exception));
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            _ => "CRIT"
        };
    }
}

/// <summary>The log folder: SYSTEM and Administrators full control, the agent account modify, nobody else (no players).</summary>
internal static class AgentLog
{
    public static string Directory => Path.Combine(AgentFiles.Directory, AgentFiles.LogDirectoryName);

    /// <summary>
    /// Creates the folder with its restricted ACL when it is missing (the installer also creates it with icacls). An existing
    /// folder keeps its ACL: the agent may not change permissions. Errors are ignored (the log is best effort).
    /// </summary>
    public static void EnsureDirectory()
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (dir.Exists) return;
            System.IO.Directory.CreateDirectory(AgentFiles.Directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (WindowsIdentity.GetCurrent().User is { } self)
                security.AddAccessRule(new FileSystemAccessRule(self, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            dir.Create(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException)
        {
            // no log folder: the agent works without its text log
        }
    }
}
