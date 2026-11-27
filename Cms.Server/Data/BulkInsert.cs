using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Cms.Server.Data;

/// <summary>
/// Fast insert of many complete rows (the demo reset writes about 13 000 rows) from the EF model (table,
/// columns, type mapping of each column), in the caller's transaction:
///  - PostgreSQL: binary COPY;
///  - SQLite: one prepared single-row INSERT per table (a statement with thousands of parameters is slow there);
///  - other providers: multi-row INSERT statements.
/// It skips the change tracker and the checks of CmsDbContext.SaveChanges, so use it only for rows that are
/// complete and valid: keys set, referenced rows written before (rows are written type by type, in the
/// order in which the types first appear).
/// </summary>
public static class BulkInsert
{
    private const int MaxParameters = 30_000;
    private const int MaxRowsPerStatement = 500;

    public static async Task<int> InsertAsync(CmsDbContext db, IEnumerable<object> rows, CancellationToken ct = default)
    {
        var total = 0;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            foreach (var group in rows.GroupBy(r => r.GetType()))
            {
                var list = group.ToList();
                var (table, columns) = Describe(db, group.Key);
                if (db.Database.GetDbConnection() is NpgsqlConnection npgsql && columns.All(c => c.Mapping.Converter == null))
                    await CopyAsync(npgsql, db, table, columns, list, ct);
                else if (db.Database.IsSqlite())
                    await InsertEachAsync(db, table, columns, list, ct);
                else
                    await InsertManyAsync(db, table, columns, list, ct);
                total += list.Count;
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        return total;
    }

    private sealed record Column(System.Reflection.PropertyInfo Clr, string Name, RelationalTypeMapping Mapping, bool Nullable);

    private static (string Table, List<Column> Columns) Describe(CmsDbContext db, Type type)
    {
        var entity = db.Model.FindEntityType(type) ?? throw new InvalidOperationException($"{type.Name} is not an entity of the model");
        var table = entity.GetTableName() ?? throw new InvalidOperationException($"{type.Name} has no table");
        var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
        var columns = new List<Column>();
        foreach (var property in entity.GetProperties())
        {
            var name = property.GetColumnName(store);
            if (name == null) continue;
            if (property.PropertyInfo == null)
                throw new InvalidOperationException($"{type.Name}.{property.Name} has no CLR property; use SaveChanges for this type");
            columns.Add(new Column(property.PropertyInfo, name, (RelationalTypeMapping)property.GetTypeMapping(), property.IsNullable));
        }
        var sql = db.GetService<ISqlGenerationHelper>();
        return (sql.DelimitIdentifier(table, entity.GetSchema()), columns);
    }

    private static string ColumnList(CmsDbContext db, List<Column> columns)
    {
        var sql = db.GetService<ISqlGenerationHelper>();
        return string.Join(", ", columns.Select(c => sql.DelimitIdentifier(c.Name)));
    }

    private static NpgsqlDbType CopyType(Type type) => (Nullable.GetUnderlyingType(type) ?? type) switch
    {
        var t when t == typeof(string) => NpgsqlDbType.Text,
        var t when t == typeof(Guid) => NpgsqlDbType.Uuid,
        var t when t == typeof(DateTimeOffset) => NpgsqlDbType.TimestampTz,
        var t when t == typeof(decimal) => NpgsqlDbType.Numeric,
        var t when t == typeof(int) => NpgsqlDbType.Integer,
        var t when t == typeof(long) => NpgsqlDbType.Bigint,
        var t when t == typeof(bool) => NpgsqlDbType.Boolean,
        var t when t == typeof(double) => NpgsqlDbType.Double,
        var t => throw new NotSupportedException($"Bulk copy of {t.Name} columns")
    };

    private static async Task CopyAsync(NpgsqlConnection connection, CmsDbContext db, string table, List<Column> columns, List<object> rows, CancellationToken ct)
    {
        var types = columns.Select(c => CopyType(c.Clr.PropertyType)).ToArray();
        await using var importer = await connection.BeginBinaryImportAsync($"COPY {table} ({ColumnList(db, columns)}) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var row in rows)
        {
            await importer.StartRowAsync(ct);
            for (var c = 0; c < columns.Count; c++)
            {
                var value = columns[c].Clr.GetValue(row);
                if (value == null) await importer.WriteNullAsync(ct);
                else await importer.WriteAsync(value, types[c], ct);
            }
        }
        await importer.CompleteAsync(ct);
    }

    private static async Task InsertEachAsync(CmsDbContext db, string table, List<Column> columns, List<object> rows, CancellationToken ct)
    {
        var sql = db.GetService<ISqlGenerationHelper>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var names = columns.Select((_, i) => sql.GenerateParameterName("p" + i)).ToList();
        command.CommandText = $"INSERT INTO {table} ({ColumnList(db, columns)}) VALUES ({string.Join(", ", names)})";
        System.Data.Common.DbParameter[]? parameters = null;
        foreach (var row in rows)
        {
            if (parameters == null)
            {
                // The type mapping configures each parameter as EF does (types of SQLite values); later rows only change the values
                parameters = columns.Select((c, i) => c.Mapping.CreateParameter(command, names[i], c.Clr.GetValue(row), c.Nullable)).ToArray();
                command.Parameters.AddRange(parameters);
                await command.PrepareAsync(ct);
            }
            else
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    var value = columns[c].Clr.GetValue(row);
                    if (value != null && columns[c].Mapping.Converter is { } converter) value = converter.ConvertToProvider(value);
                    parameters[c].Value = value ?? DBNull.Value;
                }
            }
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertManyAsync(CmsDbContext db, string table, List<Column> columns, List<object> rows, CancellationToken ct)
    {
        var sql = db.GetService<ISqlGenerationHelper>();
        var head = $"INSERT INTO {table} ({ColumnList(db, columns)}) VALUES ";
        var perStatement = Math.Clamp(MaxParameters / columns.Count, 1, MaxRowsPerStatement);
        for (var start = 0; start < rows.Count; start += perStatement)
        {
            var count = Math.Min(perStatement, rows.Count - start);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var text = new StringBuilder(head);
            var n = 0;
            for (var r = 0; r < count; r++)
            {
                text.Append(r == 0 ? "(" : ", (");
                for (var c = 0; c < columns.Count; c++)
                {
                    var name = sql.GenerateParameterName("p" + n++);
                    command.Parameters.Add(columns[c].Mapping.CreateParameter(command, name, columns[c].Clr.GetValue(rows[start + r]), columns[c].Nullable));
                    text.Append(c > 0 ? ", " : "").Append(name);
                }
                text.Append(')');
            }
            command.CommandText = text.ToString();
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
