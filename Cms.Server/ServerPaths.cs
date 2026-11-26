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
