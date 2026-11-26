using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cms.Server.Data;

/// <summary>
/// Used only by the `dotnet ef` tools. Migrations target PostgreSQL; generating them
/// does not need a running database or any credentials.
///   dotnet ef migrations add &lt;Name&gt; --project Cms.Server
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CmsDbContext>
{
    public CmsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CmsDbContext>()
            .UseNpgsql("Host=localhost;Database=cms_design")
            .Options;
        return new CmsDbContext(options);
    }
}
