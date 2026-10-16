using System.Text;
using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Persistence;

internal sealed class SqliteJudgementRepository(SqliteDb db) : IJudgementRepository
{
    public const int SubjectMaxLength = 200;

    private const string Columns = """
        id, account_id, folder, uidvalidity, uid, message_id, received_at, from_addr, subject,
        is_advertisement, confidence, summary, reason, outcome, target_item_id, target_folder,
        trial_run_id, model_name, prompt_version, duration_ms, judged_at, keep
        """;

    private const string MoveColumns = """
        id, judgement_id, account_id, from_folder, to_folder, dest_uidvalidity, dest_uid, moved_at, undone_at, undo_result
        """;

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
        await using var command = connection.CreateCommand($"SELECT {Columns} FROM judgement WHERE id = $id;", null)
            .With("$id", id);
        var records = await ReadJudgementsAsync(connection, command, ct).ConfigureAwait(false);
        return records.Count > 0 ? records[0] : null;
    }

    public async Task<IReadOnlyList<JudgementRecord>> QueryAsync(JudgementQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand("", null);

        var where = new List<string>();
        if (query.ReceivedFrom is { } from)
        {
            where.Add("received_at >= $from");
            command.With("$from", SqliteDb.FormatTimestamp(from));
        }

        if (query.ReceivedTo is { } to)
        {
            where.Add("received_at < $to");
            command.With("$to", SqliteDb.FormatTimestamp(to));
        }

        if (query.AccountId is { } accountId)
        {
            where.Add("account_id = $account");
            command.With("$account", accountId);
        }

        if (query.TrialRunId is { } trialRunId)
        {
            where.Add("trial_run_id = $trialRunId");
            command.With("$trialRunId", trialRunId);
        }

        if (query.Outcomes is { Count: > 0 } outcomes)
        {
            var names = outcomes.Select((outcome, i) =>
            {
                command.With($"$outcome{i}", outcome.ToDbValue());
                return $"$outcome{i}";
            }).ToList();
            where.Add($"outcome IN ({string.Join(", ", names)})");
        }

        var sql = new StringBuilder($"SELECT {Columns} FROM judgement");
        if (where.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", where);
        }

        sql.Append(" ORDER BY received_at DESC, id DESC LIMIT $limit OFFSET $offset;");
        command.CommandText = sql.ToString();
        command.With("$limit", Math.Max(0, query.Limit)).With("$offset", Math.Max(0, query.Offset));
        return await ReadJudgementsAsync(connection, command, ct).ConfigureAwait(false);
    }

    public async Task<MoveRecord?> GetMoveRecordAsync(long judgementId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand(
            $"SELECT {MoveColumns} FROM move_record WHERE judgement_id = $judgementId;", null)
            .With("$judgementId", judgementId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new MoveRecord
        {
            Id = reader.GetInt64(0),
            JudgementId = reader.GetInt64(1),
            Moved = new MovedMailRef(
                AccountId: reader.GetString(2),
                FromFolder: reader.GetString(3),
                ToFolder: reader.GetString(4),
                DestUidValidity: reader.GetNullableUInt32(5),
                DestUid: reader.GetNullableUInt32(6)),
            MovedAt = SqliteDb.ParseTimestamp(reader.GetString(7)),
            UndoneAt = reader.GetNullableString(8) is { } undoneAt ? SqliteDb.ParseTimestamp(undoneAt) : null,
            UndoResult = reader.GetNullableString(9) is { } result ? UndoResultExtensions.ParseUndoResult(result) : null,
        };
    }

    public async Task MarkUndoneAsync(long moveRecordId, UndoResult result, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE move_record SET undone_at = $now, undo_result = $result WHERE id = $id;", null, ct,
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
        await using var command = connection.CreateCommand("", null);
        var names = judgementIds.Select((id, i) =>
        {
            command.With($"$id{i}", id);
            return $"$id{i}";
        }).ToList();
        command.CommandText = $"""
            SELECT judgement_id, item_id, item_label, priority FROM judgement_item
            WHERE judgement_id IN ({string.Join(", ", names)})
            ORDER BY judgement_id, priority;
            """;

        var result = new Dictionary<long, List<JudgementItemRecord>>();
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
    };

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
