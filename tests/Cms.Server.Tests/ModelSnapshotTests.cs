using Cms.Server.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cms.Server.Tests;

/// <summary>The PostgreSQL migrations describe the whole model (no model change without a migration).</summary>
public sealed class ModelSnapshotTests
{
    [Fact]
    public void The_migrations_have_no_pending_model_changes()
    {
        // No connection is opened: the check compares the model with the last migration snapshot
        using var db = new CmsDbContext(new DbContextOptionsBuilder<CmsDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --project Cms.Server");
    }
}
