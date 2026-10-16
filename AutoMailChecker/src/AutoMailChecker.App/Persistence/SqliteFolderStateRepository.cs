using AutoMailChecker.Core.State;

namespace AutoMailChecker.Persistence;

internal sealed class SqliteFolderStateRepository(SqliteDb db) : IFolderStateRepository
{
    public async Task<FolderState?> GetAsync(string accountId, string folder, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand("""
            SELECT account_id, folder, uidvalidity, baseline_uid, baseline_at, last_processed_uid, updated_at
            FROM folder_state WHERE account_id = $account AND folder = $folder;
            """, null)
            .With("$account", accountId)
            .With("$folder", folder);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new FolderState(
            AccountId: reader.GetString(0),
            Folder: reader.GetString(1),
            UidValidity: reader.GetUInt32(2),
            BaselineUid: reader.GetUInt32(3),
            BaselineAt: SqliteDb.ParseTimestamp(reader.GetString(4)),
            LastProcessedUid: reader.GetUInt32(5),
            UpdatedAt: SqliteDb.ParseTimestamp(reader.GetString(6)));
    }

    public async Task UpsertAsync(FolderState state, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync("""
            INSERT INTO folder_state (account_id, folder, uidvalidity, baseline_uid, baseline_at, last_processed_uid, updated_at)
            VALUES ($account, $folder, $uidvalidity, $baselineUid, $baselineAt, $lastUid, $updatedAt)
            ON CONFLICT (account_id, folder) DO UPDATE SET
                uidvalidity = excluded.uidvalidity,
                baseline_uid = excluded.baseline_uid,
                baseline_at = excluded.baseline_at,
                last_processed_uid = excluded.last_processed_uid,
                updated_at = excluded.updated_at;
            """, null, ct,
            ("$account", state.AccountId), ("$folder", state.Folder), ("$uidvalidity", (long)state.UidValidity),
            ("$baselineUid", (long)state.BaselineUid), ("$baselineAt", SqliteDb.FormatTimestamp(state.BaselineAt)),
            ("$lastUid", (long)state.LastProcessedUid), ("$updatedAt", SqliteDb.FormatTimestamp(state.UpdatedAt)))
            .ConfigureAwait(false);
    }
}
