using AutoMailChecker.Core.State;
using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Persistence;

/// <summary>
/// SQLite（Microsoft.Data.Sqlite）による状態の保存。WAL モードで、接続は操作ごとに開く（接続プールを使う）。
/// </summary>
public sealed class SqliteStateStore : IStateStore
{
    private readonly SqliteDb _db;
    private readonly SqliteQueueRepository _queue;

    public SqliteStateStore(string databasePath, TimeProvider time)
    {
        _db = new SqliteDb(databasePath, time);
        _queue = new SqliteQueueRepository(_db);
        Folders = new SqliteFolderStateRepository(_db);
        ProcessedMessages = new SqliteProcessedMessageRepository(_db);
        Judgements = new SqliteJudgementRepository(_db);
        TrialRuns = new SqliteTrialRunRepository(_db);
    }

    public IQueueRepository Queue => _queue;
    public IFolderStateRepository Folders { get; }
    public IProcessedMessageRepository ProcessedMessages { get; }
    public IJudgementRepository Judgements { get; }
    public ITrialRunRepository TrialRuns { get; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_db.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var connection = await _db.OpenAsync(ct).ConfigureAwait(false))
        {
            await SqliteMigrations.ApplyAsync(connection, ct).ConfigureAwait(false);
        }

        await _queue.ResetProcessingAsync(ct).ConfigureAwait(false);
    }

    public async Task<PurgeResult> PurgeExpiredAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = SqliteDb.FormatTimestamp(_db.Now().AddDays(-retentionDays));
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        // 子の表（judgement_item、move_record、correction）は外部キーの ON DELETE CASCADE で一緒に消える
        var judgements = await connection.ExecuteAsync(
            "DELETE FROM judgement WHERE judged_at < $cutoff AND keep = 0;", transaction, ct, ("$cutoff", cutoff))
            .ConfigureAwait(false);
        var processed = await connection.ExecuteAsync(
            "DELETE FROM processed_message WHERE first_seen_at < $cutoff;", transaction, ct, ("$cutoff", cutoff))
            .ConfigureAwait(false);
        transaction.Commit();
        return new PurgeResult(judgements, processed);
    }

    /// <summary>テストで一時フォルダーを消す前に、プールに残った接続（ファイルの掴み）を手放す。</summary>
    internal static void ReleasePooledConnections() => SqliteConnection.ClearAllPools();
}
