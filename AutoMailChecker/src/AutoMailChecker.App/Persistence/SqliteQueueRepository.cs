using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Persistence;

internal sealed class SqliteQueueRepository(SqliteDb db) : IQueueRepository
{
    private const string Columns =
        "id, account_id, folder, uidvalidity, uid, trial_run_id, state, attempts, last_error, enqueued_at";

    public async Task<bool> EnqueueAsync(MailRef mail, long? trialRunId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        return await InsertAsync(connection, null, mail, trialRunId, db.NowText(), ct).ConfigureAwait(false);
    }

    public async Task<QueueItem?> TakeNextAsync(IReadOnlyCollection<string> skipAccounts, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand("", null);
        var skip = "";
        if (skipAccounts.Count > 0)
        {
            var names = skipAccounts.Select((account, i) =>
            {
                command.With($"$skip{i}", account);
                return $"$skip{i}";
            }).ToList();
            skip = $"AND account_id NOT IN ({string.Join(", ", names)})";
        }

        // 新着（trial_run_id が NULL）は常にお試しより先に、それぞれ古い順に処理する
        command.CommandText = $"""
            UPDATE queue_item SET state = 'processing'
            WHERE id = (
                SELECT id FROM queue_item
                WHERE state = 'waiting' {skip}
                ORDER BY (trial_run_id IS NOT NULL), id
                LIMIT 1)
            RETURNING {Columns};
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task RemoveAsync(QueueItem item, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await CompleteAsync(connection, transaction, item, ct).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task ReleaseAsync(long id, string error, int maxAttempts, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync("""
            UPDATE queue_item
            SET attempts = attempts + 1,
                last_error = $error,
                state = CASE WHEN attempts + 1 >= $max THEN 'failed' ELSE 'waiting' END
            WHERE id = $id;
            """, null, ct, ("$id", id), ("$error", error), ("$max", maxAttempts)).ConfigureAwait(false);
    }

    public async Task CompleteExcludedAsync(QueueItem item, string? messageId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        if (item.TrialRunId is null && !string.IsNullOrEmpty(messageId))
        {
            await connection.ExecuteAsync("""
                INSERT OR IGNORE INTO processed_message (account_id, message_id, excluded, first_seen_at)
                VALUES ($account, $messageId, 1, $now);
                """, transaction, ct,
                ("$account", item.Mail.AccountId), ("$messageId", messageId), ("$now", db.NowText())).ConfigureAwait(false);
        }

        await CompleteAsync(connection, transaction, item, ct).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<int> CountPendingAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand(
            "SELECT COUNT(*) FROM queue_item WHERE state IN ('waiting', 'processing');", null);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>起動時に、処理中のまま残った行を処理待ちに戻す。</summary>
    internal async Task<int> ResetProcessingAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            "UPDATE queue_item SET state = 'waiting' WHERE state = 'processing';", null, ct).ConfigureAwait(false);
    }

    internal static async Task<bool> InsertAsync(
        SqliteConnection connection, SqliteTransaction? transaction, MailRef mail, long? trialRunId, string now, CancellationToken ct)
    {
        var inserted = await connection.ExecuteAsync("""
            INSERT OR IGNORE INTO queue_item (account_id, folder, uidvalidity, uid, trial_run_id, state, attempts, enqueued_at)
            VALUES ($account, $folder, $uidvalidity, $uid, $trialRunId, 'waiting', 0, $now);
            """, transaction, ct,
            ("$account", mail.AccountId), ("$folder", mail.Folder), ("$uidvalidity", (long)mail.UidValidity),
            ("$uid", (long)mail.Uid), ("$trialRunId", trialRunId), ("$now", now)).ConfigureAwait(false);
        return inserted > 0;
    }

    /// <summary>処理を終えた1件をキューから消す。お試しの対象なら、その回の処理済み件数を進める。</summary>
    internal static async Task CompleteAsync(
        SqliteConnection connection, SqliteTransaction transaction, QueueItem item, CancellationToken ct)
    {
        await connection.ExecuteAsync("DELETE FROM queue_item WHERE id = $id;", transaction, ct, ("$id", item.Id))
            .ConfigureAwait(false);
        if (item.TrialRunId is { } trialRunId)
        {
            await connection.ExecuteAsync(
                "UPDATE trial_run SET processed = processed + 1 WHERE id = $id;", transaction, ct, ("$id", trialRunId))
                .ConfigureAwait(false);
        }
    }

    private static QueueItem Read(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        Mail: new MailRef(reader.GetString(1), reader.GetString(2), reader.GetUInt32(3), reader.GetUInt32(4)),
        TrialRunId: reader.GetNullableInt64(5),
        State: reader.GetString(6) switch
        {
            "waiting" => QueueState.Waiting,
            "processing" => QueueState.Processing,
            "failed" => QueueState.Failed,
            var other => throw new FormatException($"不明なキューの状態です: {other}"),
        },
        Attempts: reader.GetInt32(7),
        LastError: reader.GetNullableString(8),
        EnqueuedAt: SqliteDb.ParseTimestamp(reader.GetString(9)));
}
