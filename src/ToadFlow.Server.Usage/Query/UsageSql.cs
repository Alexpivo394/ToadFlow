using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Usage.Query;

/// <summary>Сырые запросы чтения: медианы и процентили считает PostgreSQL (<c>percentile_cont</c>), EF их не строит.</summary>
internal static class UsageSql
{
    /// <summary>Именованные параметры запроса (<c>@имя</c>).</summary>
    public sealed class Args : Dictionary<string, object?>;

    public static async Task<List<T>> QueryAsync<T>(ToadFlowDb db, string sql, Args args, Func<DbDataReader, T> read, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<T>();
            while (await reader.ReadAsync(ct)) rows.Add(read(reader));
            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static string? NullableString(this DbDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    public static double? NullableDouble(this DbDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToDouble(reader.GetValue(i));

    public static long Long(this DbDataReader reader, int i) => reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i));

    public static int Int(this DbDataReader reader, int i) => reader.IsDBNull(i) ? 0 : Convert.ToInt32(reader.GetValue(i));

    public static int? NullableInt(this DbDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i));

    public static long? NullableLong(this DbDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i));
}
