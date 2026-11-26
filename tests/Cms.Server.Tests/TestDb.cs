using Cms.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Tests;

/// <summary>SQLite in-memory database that lives as long as the test class instance.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public CmsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CmsDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
