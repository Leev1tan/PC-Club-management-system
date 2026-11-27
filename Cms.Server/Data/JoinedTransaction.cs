using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cms.Server.Data;

/// <summary>
/// A transaction of its own, or the open transaction of the caller (like AuditChain.AppendAsync): a service that
/// runs inside a bigger operation (a check-in starts a session, a delivered order records a bar sale) commits
/// nothing itself; the caller commits or rolls back the whole.
/// </summary>
public sealed class JoinedTransaction : IAsyncDisposable
{
    private readonly IDbContextTransaction? _own;

    private JoinedTransaction(IDbContextTransaction? own) => _own = own;

    public static async Task<JoinedTransaction> BeginAsync(CmsDbContext db, CancellationToken ct = default) =>
        new(db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null);

    public Task CommitAsync(CancellationToken ct = default) => _own?.CommitAsync(ct) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _own?.DisposeAsync() ?? ValueTask.CompletedTask;
}
