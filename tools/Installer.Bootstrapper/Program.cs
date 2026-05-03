using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

var tempDir = Path.Combine(Path.GetTempPath(), "ClubManagementSetup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);

try
{
    await using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.zip")
        ?? throw new InvalidOperationException("Embedded setup payload was not found.");

    using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
    {
        archive.ExtractToDirectory(tempDir);
    }

    var installScripts = Directory.GetFiles(tempDir, "install-*.ps1", SearchOption.TopDirectoryOnly);
    if (installScripts.Length != 1)
        throw new InvalidOperationException("Expected exactly one install-*.ps1 script in the setup payload.");

    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        WorkingDirectory = tempDir,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(installScripts[0]);
    foreach (var arg in args)
    {
        startInfo.ArgumentList.Add(arg);
    }

    var process = Process.Start(startInfo);

    if (process == null)
        throw new InvalidOperationException("Failed to start installer command.");

    await process.WaitForExitAsync();
    return process.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Setup failed: " + ex.Message);
    Console.Error.WriteLine("Extracted files are kept at: " + tempDir);
    return 1;
}
