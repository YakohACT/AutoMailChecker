using System.Text.Json;
using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Persistence;

internal sealed class SqliteTrialRunRepository(SqliteDb db) : ITrialRunRepository
{
    private const string Columns = """
        id, period_from, period_to, accounts, folders, mode, max_messages, status, total, processed,
        created_at, finished_at, rolled_back_at
        """;

    public async Task<TrialRun> CreateAsync(TrialRun run, IReadOnlyList<MailRef> targets, CancellationToken ct)
    {
        var now = db.Now();
        var nowText = SqliteDb.FormatTimestamp(now);

        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        long id;
        await using (var command = connection.CreateCommand("""
            INSERT INTO trial_run (period_from, period_to, accounts, folders, mode, max_messages, status, total, processed, created_at)
            VALUES ($from, $to, $accounts, $folders, $mode, $max, 'running', 0, 0, $now)
            RETURNING id;
            """, transaction))
        {
            command
                .With("$from", SqliteDb.FormatDate(run.PeriodFrom))
                .With("$to", SqliteDb.FormatDate(run.PeriodTo))
                .With("$accounts", JsonSerializer.Serialize(run.Accounts))
                .With("$folders", JsonSerializer.Serialize(run.Folders))
                .With("$mode", run.Mode == TrialMode.Move ? "move" : "preview")
                .With("$max", run.MaxMessages ?? 0) // 0 は上限なし
                .With("$now", nowText);
            id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        var total = 0;
        foreach (var target in targets)
        {
            if (await SqliteQueueRepository.InsertAsync(connection, transaction, target, id, nowText, ct).ConfigureAwait(false))
            {
                total++;
            }
        }

        await connection.ExecuteAsync("UPDATE trial_run SET total = $total WHERE id = $id;", transaction, ct,
            ("$total", total), ("$id", id)).ConfigureAwait(false);
        transaction.Commit();

        return run with
        {
            Id = id,
            Status = TrialRunStatus.Running,
            Total = total,
            Processed = 0,
            CreatedAt = SqliteDb.ParseTimestamp(nowText),
            FinishedAt = null,
            RolledBackAt = null,
        };
    }

    public async Task<TrialRun?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand($"SELECT {Columns} FROM trial_run WHERE id = $id;", null)
            .With("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<TrialRun>> ListAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand($"SELECT {Columns} FROM trial_run ORDER BY id DESC;", null);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var runs = new List<TrialRun>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            runs.Add(Read(reader));
        }

        return runs;
    }

    public async Task FinishAsync(long id, TrialRunStatus status, CancellationToken ct)
    {
        if (status == TrialRunStatus.Running)
        {
            throw new ArgumentException("終える回の状態には Done か Cancelled を指定してください。", nameof(status));
        }

        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        // 処理中の1件は、その処理が終わったときに消える
        await connection.ExecuteAsync(
            "DELETE FROM queue_item WHERE trial_run_id = $id AND state <> 'processing';", transaction, ct, ("$id", id))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE trial_run SET status = $status, finished_at = $now WHERE id = $id;", transaction, ct,
            ("$id", id), ("$status", status == TrialRunStatus.Done ? "done" : "cancelled"), ("$now", db.NowText()))
            .ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task MarkRolledBackAsync(long id, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync("UPDATE trial_run SET rolled_back_at = $now WHERE id = $id;", null, ct,
            ("$id", id), ("$now", db.NowText())).ConfigureAwait(false);
    }

    private static TrialRun Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        PeriodFrom = SqliteDb.ParseDate(r.GetString(1)),
        PeriodTo = SqliteDb.ParseDate(r.GetString(2)),
        Accounts = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? [],
        Folders = JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [],
        Mode = r.GetString(5) == "move" ? TrialMode.Move : TrialMode.Preview,
        MaxMessages = r.GetInt32(6) is > 0 and var max ? max : null,
        Status = r.GetString(7) switch
        {
            "running" => TrialRunStatus.Running,
            "done" => TrialRunStatus.Done,
            "cancelled" => TrialRunStatus.Cancelled,
            var other => throw new FormatException($"不明なお試しの状態です: {other}"),
        },
        Total = r.GetInt32(8),
        Processed = r.GetInt32(9),
        CreatedAt = SqliteDb.ParseTimestamp(r.GetString(10)),
        FinishedAt = r.GetNullableString(11) is { } finished ? SqliteDb.ParseTimestamp(finished) : null,
        RolledBackAt = r.GetNullableString(12) is { } rolledBack ? SqliteDb.ParseTimestamp(rolledBack) : null,
    };
}
