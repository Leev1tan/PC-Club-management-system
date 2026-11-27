namespace Cms.Server;

/// <summary>
/// Absolute data directory of the server (SQLite database, setup code, login cookie keys).
///   Storage:DataDirectory  - explicit directory; a relative path is resolved against the content root
///   default on Windows     - %ProgramData%\PCClub (C:\ProgramData\PCClub). A Windows service starts in
///                            C:\Windows\System32, so a relative path must never be used there.
///   default elsewhere      - &lt;content root&gt;/data (/app/data in the Docker image)
/// </summary>
public sealed class ServerPaths
{
    public string DataDirectory { get; }

    public ServerPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    public string Combine(string relative) => Path.Combine(DataDirectory, relative);

    /// <summary>
    /// SQLite file: Database:SqlitePath, a relative value is resolved against the data directory
    /// (never the working directory). Default: &lt;data directory&gt;/cms.db.
    /// Earlier versions used "cms.db" in the working directory (C:\Windows\System32 for a Windows
    /// service). When no path is configured and the new file does not exist yet, such a file is
    /// copied (not moved) to the new place once; the result says from where.
    /// </summary>
    public (string Path, string? CopiedFrom) ResolveSqlitePath(IConfiguration config)
    {
        var configured = config["Database:SqlitePath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = System.IO.Path.IsPathRooted(configured) ? configured : Combine(configured);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            return (System.IO.Path.GetFullPath(path), null);
        }

        var target = Combine("cms.db");
        if (File.Exists(target)) return (target, null);

        var candidates = new List<string>
        {
            System.IO.Path.Combine(Environment.CurrentDirectory, "cms.db"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "cms.db"),
        };
        if (OperatingSystem.IsWindows())
            candidates.Add(System.IO.Path.Combine(Environment.SystemDirectory, "cms.db"));

        foreach (var legacy in candidates.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(legacy) || string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(legacy, target);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(legacy + suffix)) File.Copy(legacy + suffix, target + suffix, overwrite: true);
            return (target, legacy);
        }
        return (target, null);
    }

    public static ServerPaths Resolve(IConfiguration config, string contentRoot)
    {
        var configured = config["Storage:DataDirectory"];
        string dir;
        if (!string.IsNullOrWhiteSpace(configured))
            dir = Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
        else if (OperatingSystem.IsWindows())
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCClub");
        else
            dir = Path.Combine(contentRoot, "data");

        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        return new ServerPaths(dir);
    }
}
