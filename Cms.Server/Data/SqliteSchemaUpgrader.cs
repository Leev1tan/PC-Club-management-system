using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cms.Server.Data;

/// <summary>
/// SQLite databases (Windows single-PC installs) are created with EnsureCreated(), not with
/// migrations. EnsureCreated() does nothing on an existing database, so tables, columns and
/// indexes that a newer version adds would be missing after an update.
///
/// This upgrader compares an existing SQLite database with the current model and ADDS what is
/// missing: tables, columns and indexes. It never drops, renames or changes existing objects.
/// Optional SQL can run before an index is created (for example to clean up rows that would
/// violate a new unique index).
/// </summary>
public static class SqliteSchemaUpgrader
{
    /// <summary>SQL that runs once, right before the named index is created on an existing database.</summary>
    public static readonly IReadOnlyDictionary<string, string> BeforeIndexSql =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CmsDbContext.ActiveSessionIndex] = CmsDbContext.EndDuplicateActiveSessionsSql,
        };

    public static async Task<IReadOnlyList<string>> UpgradeAsync(CmsDbContext db, CancellationToken ct = default)
    {
        var changes = new List<string>();
        var model = db.GetService<IDesignTimeModel>().Model;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var target = differ.GetDifferences(null, model.GetRelationalModel());

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var tables = await QueryNamesAsync(db, "SELECT name FROM sqlite_master WHERE type = 'table'", ct);
            var indexes = await QueryNamesAsync(db, "SELECT name FROM sqlite_master WHERE type = 'index'", ct);

            var operations = new List<MigrationOperation>();
            var preIndexSql = new List<(int Position, string Sql)>();
            foreach (var operation in target)
            {
                switch (operation)
                {
                    case CreateTableOperation table when !tables.Contains(table.Name):
                        operations.Add(table);
                        changes.Add($"table {table.Name}");
                        break;

                    case CreateTableOperation table:
                        var columns = await QueryNamesAsync(db, $"SELECT name FROM pragma_table_info('{table.Name.Replace("'", "''")}')", ct);
                        foreach (var column in table.Columns.Where(c => !columns.Contains(c.Name)))
                        {
                            operations.Add(ToAddColumn(column));
                            changes.Add($"column {table.Name}.{column.Name}");
                        }
                        break;

                    case CreateIndexOperation index when !indexes.Contains(index.Name):
                        if (tables.Contains(index.Table) && BeforeIndexSql.TryGetValue(index.Name, out var sql))
                            preIndexSql.Add((operations.Count, sql));
                        operations.Add(index);
                        changes.Add($"index {index.Name}");
                        break;
                }
            }

            if (operations.Count == 0) return changes;

            var generator = db.GetService<IMigrationsSqlGenerator>();
            var executor = db.GetService<IMigrationCommandExecutor>();
            var connection = db.GetService<IRelationalConnection>();

            // Run in order; a clean-up statement runs right before the index it belongs to
            var start = 0;
            foreach (var (position, sql) in preIndexSql.Append((operations.Count, string.Empty)))
            {
                if (position > start)
                {
                    var commands = generator.Generate(operations.GetRange(start, position - start), db.Model);
                    await executor.ExecuteNonQueryAsync(commands, connection, ct);
                    start = position;
                }
                if (!string.IsNullOrEmpty(sql))
                    await db.Database.ExecuteSqlRawAsync(sql, ct);
            }
            return changes;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// SQLite can add a NOT NULL column only with a non-null default, so a column without
    /// a default gets the CLR default of its type (0, false, empty string, empty GUID).
    /// </summary>
    private static AddColumnOperation ToAddColumn(AddColumnOperation column)
    {
        var add = new AddColumnOperation
        {
            Name = column.Name,
            Table = column.Table,
            Schema = column.Schema,
            ClrType = column.ClrType,
            ColumnType = column.ColumnType,
            IsUnicode = column.IsUnicode,
            IsFixedLength = column.IsFixedLength,
            MaxLength = column.MaxLength,
            Precision = column.Precision,
            Scale = column.Scale,
            IsRowVersion = column.IsRowVersion,
            IsNullable = column.IsNullable,
            DefaultValue = column.DefaultValue,
            DefaultValueSql = column.DefaultValueSql,
            ComputedColumnSql = column.ComputedColumnSql,
            IsStored = column.IsStored,
            Collation = column.Collation,
            Comment = column.Comment
        };
        if (!add.IsNullable && add.DefaultValue == null && add.DefaultValueSql == null && add.ComputedColumnSql == null)
        {
            add.DefaultValue = add.ClrType == typeof(string)
                ? string.Empty
                : add.ClrType.IsValueType ? Activator.CreateInstance(add.ClrType) : null;
        }
        return add;
    }

    private static async Task<HashSet<string>> QueryNamesAsync(CmsDbContext db, string sql, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }
}
