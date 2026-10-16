using AutoMailChecker.Core.State;

namespace AutoMailChecker.Persistence;

internal sealed class SqliteProcessedMessageRepository(SqliteDb db) : IProcessedMessageRepository
{
    public async Task<bool> ContainsAsync(string accountId, string messageId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand(
            "SELECT 1 FROM processed_message WHERE account_id = $account AND message_id = $messageId;", null)
            .With("$account", accountId)
            .With("$messageId", messageId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    public async Task MarkProcessedAsync(string accountId, string messageId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync("""
            INSERT OR IGNORE INTO processed_message (account_id, message_id, excluded, first_seen_at)
            VALUES ($account, $messageId, 0, $now);
            """, null, ct, ("$account", accountId), ("$messageId", messageId), ("$now", db.NowText())).ConfigureAwait(false);
    }
}
