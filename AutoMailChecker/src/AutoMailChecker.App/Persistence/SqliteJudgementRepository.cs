using System.Text;
using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Persistence;

internal sealed class SqliteJudgementRepository(SqliteDb db) : IJudgementRepository
{
    public const int SubjectMaxLength = 200;

    // 判定履歴に、移動記録（あれば）と最新の誤判定の記録を付けて読む
    private const string SelectColumns = """
        j.id, j.account_id, j.folder, j.uidvalidity, j.uid, j.message_id, j.received_at, j.from_addr, j.subject,
        j.is_advertisement, j.confidence, j.summary, j.reason, j.outcome, j.target_item_id, j.target_folder,
        j.trial_run_id, j.model_name, j.prompt_version, j.duration_ms, j.judged_at, j.keep,
        m.id, m.account_id, m.from_folder, m.to_folder, m.dest_uidvalidity, m.dest_uid, m.moved_at, m.undone_at, m.undo_result,
        EXISTS (SELECT 1 FROM correction c WHERE c.judgement_id = j.id),
        (SELECT c.correct_item_id FROM correction c WHERE c.judgement_id = j.id ORDER BY c.id DESC LIMIT 1)
        """;

    private const string FromClause = "FROM judgement j LEFT JOIN move_record m ON m.judgement_id = j.id";

    private const string MovedNotUndone = "j.outcome IN ('moved', 'moved_ad') AND m.undone_at IS NULL";

    public async Task<long> RecordAsync(JudgementRecord judgement, MoveRecord? move, QueueItem? completed, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        var judgementId = await InsertJudgementAsync(connection, transaction, judgement, ct).ConfigureAwait(false);

        foreach (var item in judgement.Items)
        {
            await connection.ExecuteAsync("""
                INSERT OR IGNORE INTO judgement_item (judgement_id, item_id, item_label, priority)
                VALUES ($judgementId, $itemId, $label, $priority);
                """, transaction, ct,
                ("$judgementId", judgementId), ("$itemId", item.ItemId.Value), ("$label", item.Label),
                ("$priority", item.Priority)).ConfigureAwait(false);
        }

        if (move is not null)
        {
            var moved = move.Moved;
            await connection.ExecuteAsync("""
                INSERT INTO move_record (judgement_id, account_id, from_folder, to_folder, dest_uidvalidity, dest_uid, moved_at)
                VALUES ($judgementId, $account, $from, $to, $destUidValidity, $destUid, $movedAt);
                """, transaction, ct,
                ("$judgementId", judgementId), ("$account", moved.AccountId), ("$from", moved.FromFolder),
                ("$to", moved.ToFolder), ("$destUidValidity", (long?)moved.DestUidValidity), ("$destUid", (long?)moved.DestUid),
                ("$movedAt", SqliteDb.FormatTimestamp(move.MovedAt))).ConfigureAwait(false);
        }

        // 新着だけを処理済みとして記録する（お試しは新着の監視と独立させる）
        if (judgement.TrialRunId is null && !string.IsNullOrEmpty(judgement.MessageId))
        {
            await connection.ExecuteAsync("""
                INSERT OR IGNORE INTO processed_message (account_id, message_id, excluded, first_seen_at)
                VALUES ($account, $messageId, 0, $now);
                """, transaction, ct,
                ("$account", judgement.Mail.AccountId), ("$messageId", judgement.MessageId), ("$now", db.NowText()))
                .ConfigureAwait(false);
        }

        if (completed is not null)
        {
            await SqliteQueueRepository.CompleteAsync(connection, transaction, completed, ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return judgementId;
    }

    public async Task<JudgementRecord?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand($"SELECT {SelectColumns} {FromClause} WHERE j.id = $id;", null)
            .With("$id", id);
        var records = await ReadJudgementsAsync(connection, command, ct).ConfigureAwait(false);
        return records.Count > 0 ? records[0] : null;
    }

    public async Task<IReadOnlyList<JudgementRecord>> QueryAsync(JudgementQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand("", null);
        var where = BuildWhere(query, command);
        command.CommandText =
            $"SELECT {SelectColumns} {FromClause}{where} ORDER BY j.received_at DESC, j.id DESC LIMIT $limit OFFSET $offset;";
        command.With("$limit", Math.Max(0, query.Limit)).With("$offset", Math.Max(0, query.Offset));
        return await ReadJudgementsAsync(connection, command, ct).ConfigureAwait(false);
    }

    public async Task<HistorySummary> SummarizeAsync(JudgementQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand("", null);
        var where = BuildWhere(query, command);
        command.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN {MovedNotUndone} THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN j.confidence = 'low' THEN 1 ELSE 0 END), 0)
            {FromClause}{where};
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var total = reader.GetInt32(0);
        var moved = reader.GetInt32(1);
        // 移動していないものは、すべて受信トレイに残っている（移動予定・判定失敗・戻したものを含む）
        return new HistorySummary(total, moved, total - moved, reader.GetInt32(2));
    }

    public async Task<TimeSpan?> AverageDurationAsync(int sampleSize, int minSamples, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand("""
            SELECT COUNT(*), AVG(duration_ms) FROM (
                SELECT duration_ms FROM judgement
                WHERE duration_ms IS NOT NULL AND outcome NOT IN ('failed', 'recorded')
                ORDER BY id DESC LIMIT $n);
            """, null).With("$n", sampleSize);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return reader.GetInt32(0) >= Math.Max(1, minSamples) && !reader.IsDBNull(1)
            ? TimeSpan.FromMilliseconds(reader.GetDouble(1))
            : null;
    }

    public async Task<MoveRecord?> GetMoveRecordAsync(long judgementId, CancellationToken ct)
    {
        var judgement = await GetAsync(judgementId, ct).ConfigureAwait(false);
        return judgement?.Move;
    }

    public async Task MarkUndoneAsync(long moveRecordId, UndoResult result, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        // 失敗したときは戻した日時を入れず、もう一度試せるようにする
        await connection.ExecuteAsync("""
            UPDATE move_record
            SET undo_result = $result,
                undone_at = CASE WHEN $result IN ('restored', 'not_found') THEN $now ELSE undone_at END
            WHERE id = $id;
            """, null, ct,
            ("$id", moveRecordId), ("$now", db.NowText()), ("$result", result.ToDbValue())).ConfigureAwait(false);
    }

    public async Task AddCorrectionAsync(long judgementId, string? correctItemId, CorrectionSource source, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("""
            INSERT INTO correction (judgement_id, correct_item_id, source, created_at)
            VALUES ($judgementId, $correctItemId, $source, $now);
            """, transaction, ct,
            ("$judgementId", judgementId), ("$correctItemId", correctItemId),
            ("$source", source == CorrectionSource.Undo ? "undo" : "manual"), ("$now", db.NowText())).ConfigureAwait(false);
        // 誤判定が記録された判定は、精度の確認や評価セットの材料として保持期間後も残す
        await connection.ExecuteAsync("UPDATE judgement SET keep = 1 WHERE id = $id;", transaction, ct, ("$id", judgementId))
            .ConfigureAwait(false);
        transaction.Commit();
    }

    private static string BuildWhere(JudgementQuery query, SqliteCommand command)
    {
        var where = new List<string>();
        if (query.ReceivedFrom is { } from)
        {
            where.Add("j.received_at >= $from");
            command.With("$from", SqliteDb.FormatTimestamp(from));
        }

        if (query.ReceivedTo is { } to)
        {
            where.Add("j.received_at < $to");
            command.With("$to", SqliteDb.FormatTimestamp(to));
        }

        if (query.AccountId is { } accountId)
        {
            where.Add("j.account_id = $account");
            command.With("$account", accountId);
        }

        if (query.TrialRunId is { } trialRunId)
        {
            where.Add("j.trial_run_id = $trialRunId");
            command.With("$trialRunId", trialRunId);
        }

        if (query.NewMailOnly)
        {
            where.Add("j.trial_run_id IS NULL");
        }

        if (query.Outcomes is { Count: > 0 } outcomes)
        {
            var names = outcomes.Select((outcome, i) =>
            {
                command.With($"$outcome{i}", outcome.ToDbValue());
                return $"$outcome{i}";
            }).ToList();
            where.Add($"j.outcome IN ({string.Join(", ", names)})");
        }

        var category = query.Category switch
        {
            HistoryCategory.Moved => MovedNotUndone,
            HistoryCategory.Planned => "j.outcome = 'planned'",
            HistoryCategory.Kept => "NOT (j.outcome IN ('moved', 'moved_ad', 'planned', 'failed'))",
            HistoryCategory.Advertisement => "j.is_advertisement = 1",
            HistoryCategory.Failed => "j.outcome = 'failed'",
            HistoryCategory.Undone => "m.undone_at IS NOT NULL",
            _ => null,
        };
        if (category is not null)
        {
            where.Add($"({category})");
        }

        return where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
    }

    private static async Task<long> InsertJudgementAsync(
        SqliteConnection connection, SqliteTransaction transaction, JudgementRecord j, CancellationToken ct)
    {
        await using var command = connection.CreateCommand("""
            INSERT INTO judgement (
                account_id, folder, uidvalidity, uid, message_id, received_at, from_addr, subject,
                is_advertisement, confidence, summary, reason, outcome, target_item_id, target_folder,
                trial_run_id, model_name, prompt_version, duration_ms, judged_at, keep)
            VALUES (
                $account, $folder, $uidvalidity, $uid, $messageId, $receivedAt, $from, $subject,
                $isAd, $confidence, $summary, $reason, $outcome, $targetItemId, $targetFolder,
                $trialRunId, $modelName, $promptVersion, $durationMs, $judgedAt, $keep)
            RETURNING id;
            """, transaction)
            .With("$account", j.Mail.AccountId)
            .With("$folder", j.Mail.Folder)
            .With("$uidvalidity", (long)j.Mail.UidValidity)
            .With("$uid", (long)j.Mail.Uid)
            .With("$messageId", j.MessageId)
            .With("$receivedAt", j.ReceivedAt is { } received ? SqliteDb.FormatTimestamp(received) : null)
            .With("$from", j.From)
            .With("$subject", Truncate(j.Subject, SubjectMaxLength))
            .With("$isAd", j.IsAdvertisement ? 1 : 0)
            .With("$confidence", j.Confidence?.ToDbValue())
            .With("$summary", j.Summary)
            .With("$reason", j.Reason)
            .With("$outcome", j.Outcome.ToDbValue())
            .With("$targetItemId", j.TargetItemId)
            .With("$targetFolder", j.TargetFolder)
            .With("$trialRunId", j.TrialRunId)
            .With("$modelName", j.ModelName)
            .With("$promptVersion", j.PromptVersion)
            .With("$durationMs", j.DurationMs)
            .With("$judgedAt", SqliteDb.FormatTimestamp(j.JudgedAt))
            .With("$keep", j.Keep ? 1 : 0);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task<List<JudgementRecord>> ReadJudgementsAsync(
        SqliteConnection connection, SqliteCommand command, CancellationToken ct)
    {
        var records = new List<JudgementRecord>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                records.Add(ReadJudgement(reader));
            }
        }

        if (records.Count == 0)
        {
            return records;
        }

        var itemsByJudgement = await ReadItemsAsync(connection, records.Select(r => r.Id).ToList(), ct).ConfigureAwait(false);
        return records
            .Select(r => itemsByJudgement.TryGetValue(r.Id, out var items) ? r with { Items = items } : r)
            .ToList();
    }

    private static async Task<Dictionary<long, IReadOnlyList<JudgementItemRecord>>> ReadItemsAsync(
        SqliteConnection connection, IReadOnlyList<long> judgementIds, CancellationToken ct)
    {
        var result = new Dictionary<long, List<JudgementItemRecord>>();

        // SQLite の引数の数の上限に当たらないよう、まとめて読む件数を区切る
        foreach (var chunk in judgementIds.Chunk(500))
        {
            await using var command = connection.CreateCommand("", null);
            var names = chunk.Select((id, i) =>
            {
                command.With($"$id{i}", id);
                return $"$id{i}";
            }).ToList();
            command.CommandText = $"""
                SELECT judgement_id, item_id, item_label, priority FROM judgement_item
                WHERE judgement_id IN ({string.Join(", ", names)})
                ORDER BY judgement_id, priority;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var judgementId = reader.GetInt64(0);
                if (!result.TryGetValue(judgementId, out var list))
                {
                    result[judgementId] = list = [];
                }

                list.Add(new JudgementItemRecord(new ItemId(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3)));
            }
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<JudgementItemRecord>)p.Value);
    }

    private static JudgementRecord ReadJudgement(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Mail = new MailRef(r.GetString(1), r.GetString(2), r.GetUInt32(3), r.GetUInt32(4)),
        MessageId = r.GetNullableString(5),
        ReceivedAt = r.GetNullableString(6) is { } received ? SqliteDb.ParseTimestamp(received) : null,
        From = r.GetNullableString(7),
        Subject = r.GetNullableString(8),
        IsAdvertisement = r.GetInt64(9) != 0,
        Confidence = r.GetNullableString(10) is { } confidence ? ConfidenceExtensions.ParseConfidence(confidence) : null,
        Summary = r.GetNullableString(11),
        Reason = r.GetNullableString(12),
        Outcome = OutcomeExtensions.ParseOutcome(r.GetString(13)),
        TargetItemId = r.GetNullableString(14),
        TargetFolder = r.GetNullableString(15),
        TrialRunId = r.GetNullableInt64(16),
        ModelName = r.GetNullableString(17),
        PromptVersion = r.GetNullableString(18),
        DurationMs = r.GetNullableInt64(19),
        JudgedAt = SqliteDb.ParseTimestamp(r.GetString(20)),
        Keep = r.GetInt64(21) != 0,
        Move = r.IsDBNull(22) ? null : new MoveRecord
        {
            Id = r.GetInt64(22),
            JudgementId = r.GetInt64(0),
            Moved = new MovedMailRef(
                AccountId: r.GetString(23),
                FromFolder: r.GetString(24),
                ToFolder: r.GetString(25),
                DestUidValidity: r.GetNullableUInt32(26),
                DestUid: r.GetNullableUInt32(27)),
            MovedAt = SqliteDb.ParseTimestamp(r.GetString(28)),
            UndoneAt = r.GetNullableString(29) is { } undoneAt ? SqliteDb.ParseTimestamp(undoneAt) : null,
            UndoResult = r.GetNullableString(30) is { } result ? UndoResultExtensions.ParseUndoResult(result) : null,
        },
        IsCorrected = r.GetInt64(31) != 0,
        CorrectItemId = r.GetNullableString(32),
    };

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
