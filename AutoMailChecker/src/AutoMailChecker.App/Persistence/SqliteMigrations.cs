using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Persistence;

/// <summary>
/// スキーマの版を PRAGMA user_version に持ち、起動時に不足分の移行 SQL を順に当てる。
/// 既存の版の SQL は書き換えず、変更は新しい版として末尾に足すこと。
/// </summary>
internal static class SqliteMigrations
{
    /// <summary>添字 i の SQL を当てると user_version が i + 1 になる。</summary>
    private static readonly string[] Steps =
    [
        // 版1: E-R 図の8つの表
        """
        CREATE TABLE trial_run (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            period_from     TEXT    NOT NULL,
            period_to       TEXT    NOT NULL,
            accounts        TEXT    NOT NULL,
            folders         TEXT    NOT NULL,
            mode            TEXT    NOT NULL,
            max_messages    INTEGER NOT NULL,
            status          TEXT    NOT NULL,
            total           INTEGER NOT NULL DEFAULT 0,
            processed       INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT    NOT NULL,
            finished_at     TEXT,
            rolled_back_at  TEXT
        );

        CREATE TABLE judgement (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id        TEXT    NOT NULL,
            folder            TEXT    NOT NULL,
            uidvalidity       INTEGER NOT NULL,
            uid               INTEGER NOT NULL,
            message_id        TEXT,
            received_at       TEXT,
            from_addr         TEXT,
            subject           TEXT,
            is_advertisement  INTEGER NOT NULL DEFAULT 0,
            confidence        TEXT,
            summary           TEXT,
            reason            TEXT,
            outcome           TEXT    NOT NULL,
            target_item_id    TEXT,
            target_folder     TEXT,
            trial_run_id      INTEGER REFERENCES trial_run(id),
            model_name        TEXT,
            prompt_version    TEXT,
            duration_ms       INTEGER,
            judged_at         TEXT    NOT NULL,
            keep              INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE judgement_item (
            judgement_id  INTEGER NOT NULL REFERENCES judgement(id) ON DELETE CASCADE,
            item_id       TEXT    NOT NULL,
            item_label    TEXT    NOT NULL,
            priority      INTEGER NOT NULL,
            PRIMARY KEY (judgement_id, item_id)
        ) WITHOUT ROWID;

        CREATE TABLE move_record (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            judgement_id      INTEGER NOT NULL UNIQUE REFERENCES judgement(id) ON DELETE CASCADE,
            account_id        TEXT    NOT NULL,
            from_folder       TEXT    NOT NULL,
            to_folder         TEXT    NOT NULL,
            dest_uidvalidity  INTEGER,
            dest_uid          INTEGER,
            moved_at          TEXT    NOT NULL,
            undone_at         TEXT,
            undo_result       TEXT
        );

        CREATE TABLE correction (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            judgement_id     INTEGER NOT NULL REFERENCES judgement(id) ON DELETE CASCADE,
            correct_item_id  TEXT,
            source           TEXT    NOT NULL,
            created_at       TEXT    NOT NULL
        );

        CREATE TABLE queue_item (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id    TEXT    NOT NULL,
            folder        TEXT    NOT NULL,
            uidvalidity   INTEGER NOT NULL,
            uid           INTEGER NOT NULL,
            trial_run_id  INTEGER REFERENCES trial_run(id),
            state         TEXT    NOT NULL DEFAULT 'waiting',
            attempts      INTEGER NOT NULL DEFAULT 0,
            last_error    TEXT,
            enqueued_at   TEXT    NOT NULL
        );

        -- SQLite の一意制約は NULL どうしを別物として扱うため、IFNULL で 0 にした式で張る
        CREATE UNIQUE INDEX ux_queue_item_mail
            ON queue_item (account_id, folder, uidvalidity, uid, IFNULL(trial_run_id, 0));

        CREATE TABLE folder_state (
            account_id          TEXT    NOT NULL,
            folder              TEXT    NOT NULL,
            uidvalidity         INTEGER NOT NULL,
            baseline_uid        INTEGER NOT NULL,
            baseline_at         TEXT    NOT NULL,
            last_processed_uid  INTEGER NOT NULL,
            updated_at          TEXT    NOT NULL,
            PRIMARY KEY (account_id, folder)
        ) WITHOUT ROWID;

        CREATE TABLE processed_message (
            account_id     TEXT    NOT NULL,
            message_id     TEXT    NOT NULL,
            excluded       INTEGER NOT NULL DEFAULT 0,
            first_seen_at  TEXT    NOT NULL,
            PRIMARY KEY (account_id, message_id)
        ) WITHOUT ROWID;

        CREATE INDEX ix_judgement_judged_at       ON judgement (judged_at);
        CREATE INDEX ix_judgement_account_message ON judgement (account_id, message_id);
        CREATE INDEX ix_judgement_trial_run       ON judgement (trial_run_id);
        CREATE INDEX ix_queue_item_state          ON queue_item (state, id);
        CREATE INDEX ix_processed_message_seen    ON processed_message (first_seen_at);
        """,
    ];

    public static int LatestVersion => Steps.Length;

    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken ct)
    {
        // WAL はファイルに記録されるので、1回設定すれば以後も有効
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;", null, ct).ConfigureAwait(false);

        var current = await GetVersionAsync(connection, ct).ConfigureAwait(false);
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"状態 DB の版（{current}）がこのアプリの対応する版（{LatestVersion}）より新しいため開けません。新しい版のアプリを使ってください。");
        }

        for (var version = current; version < LatestVersion; version++)
        {
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(Steps[version], transaction, ct).ConfigureAwait(false);
            await connection.ExecuteAsync($"PRAGMA user_version = {version + 1};", transaction, ct).ConfigureAwait(false);
            transaction.Commit();
        }
    }

    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand("PRAGMA user_version;", null);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
