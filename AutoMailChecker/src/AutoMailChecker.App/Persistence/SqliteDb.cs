using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Persistence;

/// <summary>接続の作成と、日時の書式をまとめる。日時はすべて ISO 8601 の文字列（UTC）で保存する。</summary>
internal sealed class SqliteDb(string databasePath, TimeProvider time)
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string DateFormat = "yyyy-MM-dd";

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    public string DatabasePath { get; } = databasePath;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    public DateTimeOffset Now() => time.GetUtcNow();

    public string NowText() => FormatTimestamp(Now());

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string FormatDate(DateOnly value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);
}

internal static class SqliteCommandExtensions
{
    public static SqliteCommand CreateCommand(this SqliteConnection connection, string sql, SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static async Task<int> ExecuteAsync(
        this SqliteConnection connection, string sql, SqliteTransaction? transaction, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand(sql, transaction);
        foreach (var (name, value) in parameters)
        {
            command.With(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static uint? GetNullableUInt32(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : checked((uint)reader.GetInt64(ordinal));

    public static uint GetUInt32(this SqliteDataReader reader, int ordinal) => checked((uint)reader.GetInt64(ordinal));
}
