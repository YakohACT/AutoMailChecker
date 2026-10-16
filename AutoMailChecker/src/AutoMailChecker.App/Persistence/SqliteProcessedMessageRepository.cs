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
}
