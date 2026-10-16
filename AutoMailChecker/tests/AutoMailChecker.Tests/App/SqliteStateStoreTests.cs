using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using AutoMailChecker.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoMailChecker.Tests.App;

public sealed class SqliteStateStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(Start);
    private readonly string _dbPath;
    private readonly SqliteStateStore _store;

    public SqliteStateStoreTests()
    {
        _dbPath = _temp.Combine("state.db");
        _store = new SqliteStateStore(_dbPath, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 初期化で8つの表を作り_スキーマの版を最新にする()
    {
        await _store.InitializeAsync(Ct);
        await _store.InitializeAsync(Ct); // 2回目は何もしない

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(Ct);
        Assert.Equal(SqliteMigrations.LatestVersion, await SqliteMigrations.GetVersionAsync(connection, Ct));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            ["correction", "folder_state", "judgement", "judgement_item", "move_record", "processed_message", "queue_item", "trial_run"],
            tables);
    }

    [Fact]
    public async Task 同じメールは二重に積まない_お試しの対象としては別に積める()
    {
        await _store.InitializeAsync(Ct);
        var mail = Mail(10);

        Assert.True(await _store.Queue.EnqueueAsync(mail, null, Ct));
        Assert.False(await _store.Queue.EnqueueAsync(mail, null, Ct));
        var trial = await CreateTrialAsync([mail]);

        Assert.Equal(1, trial.Total);
        Assert.Equal(2, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 新着はお試しより先に_それぞれ古い順に処理する()
    {
        await _store.InitializeAsync(Ct);
        await CreateTrialAsync([Mail(1), Mail(2)]);
        await _store.Queue.EnqueueAsync(Mail(20), null, Ct);
        await _store.Queue.EnqueueAsync(Mail(21), null, Ct);

        var order = new List<uint>();
        while (await _store.Queue.TakeNextAsync(Ct) is { } item)
        {
            Assert.Equal(QueueState.Processing, item.State);
            order.Add(item.Mail.Uid);
        }

        Assert.Equal([20u, 21u, 1u, 2u], order);
    }

    [Fact]
    public async Task 処理できなかった1件は再試行し_上限に達したらfailedにする()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(Mail(5), null, Ct);

        var first = await _store.Queue.TakeNextAsync(Ct);
        await _store.Queue.ReleaseAsync(first!.Id, "接続が切れました", maxAttempts: 2, Ct);
        var second = await _store.Queue.TakeNextAsync(Ct);
        await _store.Queue.ReleaseAsync(second!.Id, "接続が切れました", maxAttempts: 2, Ct);

        Assert.Equal(1, second.Attempts);
        Assert.Equal("接続が切れました", second.LastError);
        Assert.Null(await _store.Queue.TakeNextAsync(Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 起動時に処理中のまま残った行を処理待ちに戻す()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(Mail(7), null, Ct);
        var taken = await _store.Queue.TakeNextAsync(Ct);

        var restarted = new SqliteStateStore(_dbPath, _time);
        await restarted.InitializeAsync(Ct);

        var again = await restarted.Queue.TakeNextAsync(Ct);
        Assert.Equal(taken!.Id, again?.Id);
    }

    [Fact]
    public async Task 除外したメールは処理済みとして記録してキューから消す()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(Mail(8), null, Ct);
        var item = await _store.Queue.TakeNextAsync(Ct);

        await _store.Queue.CompleteExcludedAsync(item!, "<excluded@example.com>", Ct);

        Assert.True(await _store.ProcessedMessages.ContainsAsync("gmail1", "<excluded@example.com>", Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 判定1件分を1つのトランザクションで書く()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(Mail(30), null, Ct);
        var item = await _store.Queue.TakeNextAsync(Ct);
        var judgement = Judgement(item!.Mail) with { Subject = new string('件', 250) };
        var move = Move(destUid: 501);

        var id = await _store.Judgements.RecordAsync(judgement, move, item, Ct);

        var saved = await _store.Judgements.GetAsync(id, Ct);
        Assert.NotNull(saved);
        Assert.Equal(200, saved.Subject!.Length);
        Assert.Equal(Outcome.Moved, saved.Outcome);
        Assert.Equal(Confidence.High, saved.Confidence);
        Assert.Equal(["important", "reservation"], saved.Items.Select(i => i.ItemId.Value));
        Assert.Equal(judgement.ReceivedAt, saved.ReceivedAt);

        var moveRecord = await _store.Judgements.GetMoveRecordAsync(id, Ct);
        Assert.Equal(501u, moveRecord?.Moved.DestUid);
        Assert.Equal("AI/重要", moveRecord?.Moved.ToFolder);

        Assert.True(await _store.ProcessedMessages.ContainsAsync("gmail1", judgement.MessageId!, Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 途中で失敗したら何も書かない()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(Mail(31), null, Ct);
        var item = await _store.Queue.TakeNextAsync(Ct);
        // 移動先が NULL だと move_record の NOT NULL 制約で失敗する
        var broken = new MoveRecord { Moved = new MovedMailRef("gmail1", "INBOX", null!, null, null), MovedAt = Start };

        await Assert.ThrowsAsync<SqliteException>(() => _store.Judgements.RecordAsync(Judgement(item!.Mail), broken, item, Ct));

        Assert.Empty(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.Equal(1, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task お試しの判定は回の進み具合を進め_処理済みには記録しない()
    {
        await _store.InitializeAsync(Ct);
        var trial = await CreateTrialAsync([Mail(40), Mail(41)]);
        var item = await _store.Queue.TakeNextAsync(Ct);

        await _store.Judgements.RecordAsync(Judgement(item!.Mail) with { TrialRunId = trial.Id, Outcome = Outcome.Planned }, null, item, Ct);

        var updated = await _store.TrialRuns.GetAsync(trial.Id, Ct);
        Assert.Equal(1, updated?.Processed);
        Assert.Equal(2, updated?.Total);
        Assert.False(await _store.ProcessedMessages.ContainsAsync("gmail1", "<m40@example.com>", Ct));
    }

    [Fact]
    public async Task 保持期間を過ぎた履歴は消し_誤判定を記録したものは残す()
    {
        await _store.InitializeAsync(Ct);
        var old = await _store.Judgements.RecordAsync(Judgement(Mail(50)) with { JudgedAt = Start }, Move(), null, Ct);
        var corrected = await _store.Judgements.RecordAsync(Judgement(Mail(51)) with { JudgedAt = Start }, null, null, Ct);
        await _store.Judgements.AddCorrectionAsync(corrected, "payment", CorrectionSource.Manual, Ct);
        _time.Advance(TimeSpan.FromDays(91));
        var recent = await _store.Judgements.RecordAsync(Judgement(Mail(52)) with { JudgedAt = _time.Now }, null, null, Ct);

        var result = await _store.PurgeExpiredAsync(90, Ct);

        Assert.Equal(1, result.Judgements);
        Assert.Equal(2, result.ProcessedMessages); // 50 と 51 の記録（52 は新しい）
        Assert.Null(await _store.Judgements.GetAsync(old, Ct));
        Assert.Null(await _store.Judgements.GetMoveRecordAsync(old, Ct)); // 子の表も一緒に消える
        Assert.True((await _store.Judgements.GetAsync(corrected, Ct))?.Keep);
        Assert.NotNull(await _store.Judgements.GetAsync(recent, Ct));
    }

    [Fact]
    public async Task 元に戻した結果を記録する()
    {
        await _store.InitializeAsync(Ct);
        var id = await _store.Judgements.RecordAsync(Judgement(Mail(60)), Move(), null, Ct);
        var move = await _store.Judgements.GetMoveRecordAsync(id, Ct);
        _time.Advance(TimeSpan.FromHours(1));

        await _store.Judgements.MarkUndoneAsync(move!.Id, UndoResult.Restored, Ct);

        var undone = await _store.Judgements.GetMoveRecordAsync(id, Ct);
        Assert.Equal(UndoResult.Restored, undone?.UndoResult);
        Assert.Equal(_time.Now, undone?.UndoneAt);
    }

    [Fact]
    public async Task 判定履歴を絞り込んで新しい順に返す()
    {
        await _store.InitializeAsync(Ct);
        await _store.Judgements.RecordAsync(Judgement(Mail(70), "gmail1", Start.AddHours(1)), null, null, Ct);
        await _store.Judgements.RecordAsync(Judgement(Mail(71), "gmail1", Start.AddHours(3)) with { Outcome = Outcome.KeptAd }, null, null, Ct);
        await _store.Judgements.RecordAsync(Judgement(Mail(72), "gmail1", Start.AddHours(2)), null, null, Ct);
        await _store.Judgements.RecordAsync(Judgement(Mail(73, "icloud"), "icloud", Start.AddHours(4)), null, null, Ct);

        var moved = await _store.Judgements.QueryAsync(
            new JudgementQuery { AccountId = "gmail1", Outcomes = [Outcome.Moved] }, Ct);
        var page = await _store.Judgements.QueryAsync(new JudgementQuery { Offset = 1, Limit = 2 }, Ct);

        Assert.Equal([72u, 70u], moved.Select(j => j.Mail.Uid));
        Assert.Equal([71u, 72u], page.Select(j => j.Mail.Uid));
    }

    [Fact]
    public async Task 監視フォルダーの状態を保存して読み直せる()
    {
        await _store.InitializeAsync(Ct);
        var state = new FolderState("icloud", "INBOX", 12345, 900, Start, 900, Start);

        await _store.Folders.UpsertAsync(state, Ct);
        await _store.Folders.UpsertAsync(state with { LastProcessedUid = 950, UpdatedAt = Start.AddMinutes(5) }, Ct);

        var saved = await _store.Folders.GetAsync("icloud", "INBOX", Ct);
        Assert.Equal(state with { LastProcessedUid = 950, UpdatedAt = Start.AddMinutes(5) }, saved);
        Assert.Null(await _store.Folders.GetAsync("icloud", "Archive", Ct));
    }

    [Fact]
    public async Task お試しを中止するとキューに残った分を消す()
    {
        await _store.InitializeAsync(Ct);
        var trial = await CreateTrialAsync([Mail(80), Mail(81), Mail(82)]);

        await _store.TrialRuns.FinishAsync(trial.Id, TrialRunStatus.Cancelled, Ct);
        await _store.TrialRuns.MarkRolledBackAsync(trial.Id, Ct);

        var saved = await _store.TrialRuns.GetAsync(trial.Id, Ct);
        Assert.Equal(TrialRunStatus.Cancelled, saved?.Status);
        Assert.NotNull(saved?.FinishedAt);
        Assert.NotNull(saved?.RolledBackAt);
        Assert.Equal(["gmail1"], saved?.Accounts);
        Assert.Equal(new DateOnly(2026, 9, 1), saved?.PeriodFrom);
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
        Assert.Single(await _store.TrialRuns.ListAsync(Ct));
    }

    private static MailRef Mail(uint uid, string account = "gmail1") => new(account, "INBOX", 777, uid);

    private Task<TrialRun> CreateTrialAsync(IReadOnlyList<MailRef> targets) =>
        _store.TrialRuns.CreateAsync(new TrialRun
        {
            PeriodFrom = new DateOnly(2026, 9, 1),
            PeriodTo = new DateOnly(2026, 9, 30),
            Accounts = ["gmail1"],
            Folders = ["INBOX"],
            Mode = TrialMode.Preview,
            MaxMessages = 200,
        }, targets, Ct);

    private JudgementRecord Judgement(MailRef mail, string account = "gmail1", DateTimeOffset? received = null) => new()
    {
        Mail = mail with { AccountId = account },
        MessageId = $"<m{mail.Uid}@example.com>",
        ReceivedAt = received ?? Start.AddMinutes(-10),
        From = "〇〇ホテル <info@example.com>",
        Subject = "ご予約確定のお知らせ",
        IsAdvertisement = false,
        Confidence = Confidence.High,
        Summary = "10月20日宿泊の予約確定",
        Reason = "宿泊予約の確定通知のため",
        Outcome = Outcome.Moved,
        TargetItemId = "important",
        TargetFolder = "AI/重要",
        ModelName = "Qwen3.5-9B",
        PromptVersion = "p1",
        DurationMs = 12000,
        JudgedAt = _time.Now,
        Items =
        [
            new JudgementItemRecord(new ItemId("reservation"), "予約関連", 2),
            new JudgementItemRecord(new ItemId("important"), "重要", 1),
        ],
    };

    private MoveRecord Move(uint? destUid = null) => new()
    {
        Moved = new MovedMailRef("gmail1", "INBOX", "AI/重要", destUid is null ? null : 888u, destUid),
        MovedAt = _time.Now,
    };
}
