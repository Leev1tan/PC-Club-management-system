using Microsoft.Extensions.Configuration;
using Xunit;

namespace Cms.Server.Tests;

public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccms-paths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Paths_are_absolute_and_relative_values_use_the_data_directory()
    {
        var paths = ServerPaths.Resolve(Config(("Storage:DataDirectory", Path.Combine(_root, "data"))), _root);
        Assert.True(Path.IsPathRooted(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.DataDirectory));

        var (path, copied) = paths.ResolveSqlitePath(Config(("Database:SqlitePath", "db/club.db")));
        Assert.Equal(Path.Combine(paths.DataDirectory, "db", "club.db"), path);
        Assert.Null(copied);

        var (fallback, _) = paths.ResolveSqlitePath(Config());
        Assert.Equal(Path.Combine(paths.DataDirectory, "cms.db"), fallback);
    }

    [Fact]
    public void A_relative_data_directory_uses_the_content_root()
    {
        var paths = ServerPaths.Resolve(Config(("Storage:DataDirectory", "store")), _root);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "store")), paths.DataDirectory);
    }

    [Fact]
    public void The_database_of_an_earlier_version_is_copied_once()
    {
        var paths = ServerPaths.Resolve(Config(("Storage:DataDirectory", Path.Combine(_root, "new"))), _root);
        var legacy = Path.Combine(Environment.CurrentDirectory, "cms.db");
        var existed = File.Exists(legacy);
        if (existed) return; // never touch a real file in the working directory
        try
        {
            File.WriteAllText(legacy, "legacy");
            var (path, copied) = paths.ResolveSqlitePath(Config());
            Assert.Equal(Path.GetFullPath(legacy), copied);
            Assert.Equal("legacy", File.ReadAllText(path));
            Assert.True(File.Exists(legacy)); // copied, not moved

            Assert.Null(paths.ResolveSqlitePath(Config()).CopiedFrom); // only once
        }
        finally
        {
            File.Delete(legacy);
        }
    }
}
