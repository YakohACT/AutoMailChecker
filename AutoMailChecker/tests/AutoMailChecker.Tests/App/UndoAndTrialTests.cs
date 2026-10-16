using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Persistence;
using AutoMailChecker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.App;

public sealed class UndoAndTrialTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly SqliteStateStore _store;
    private readonly FakeAccounts _accounts = new();
    private readonly FixedConfig _config = new(DefaultConfig.Instance);
    private readonly AppStatusService _status;
    private readonly UndoService _undo;
    private readonly TrialRunner _trials;

    public UndoAndTrialTests()
    {
        _store = new SqliteStateStore(_temp.Combine("state.db"), _time);
        _status = new AppStatusService(_config);
        _undo = new UndoService(_store, _accounts, NullLogger<UndoService>.Instance);
        _trials = new TrialRunner(_store, _accounts, _undo, _config, new QueueSignal(), _status, NullLogger<TrialRunner>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeMailbox Gmail => _accounts.Mailbox("gmail1");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 受信トレイに戻すと_誤判定として記録し_新着として再び仕分けない()
    {
        await _store.InitializeAsync(Ct);
        var id = await MoveAndRecordAsync(10, "<m10@example.com>");

        var outcome = await _undo.UndoAsync(id, asMisjudgement: true, Ct);

        Assert.Equal(UndoResult.Restored, outcome.Result);
        var record = await _store.Judgements.GetAsync(id, Ct);
        Assert.Equal(UndoResult.Restored, record?.Move?.UndoResult);
        Assert.NotNull(record?.Move?.UndoneAt);
        Assert.True(record?.IsCorrected);
        Assert.Null(record?.CorrectItemId); // どの項目でもない（受信トレイに残すべきだった）
        Assert.True(record?.Keep);
        Assert.True(await _store.ProcessedMessages.ContainsAsync("gmail1", "<m10@example.com>", Ct));
        Assert.Contains(Gmail.Mails.Values, h => h.Ref.Folder == "INBOX" && h.MessageId == "<m10@example.com>");
    }

    [Fact]
    public async Task COPYUIDがなくてもMessageIDで探して戻す()
    {
        await _store.InitializeAsync(Ct);
        Gmail.ReturnCopyUid = false;
        var id = await MoveAndRecordAsync(11, "<m11@example.com>");

        var outcome = await _undo.UndoAsync(id, asMisjudgement: true, Ct);

        Assert.Equal(UndoResult.Restored, outcome.Result);
    }

    [Fact]
    public async Task すでに別の場所へ移されていたら何もせず_その旨を返す()
    {
        await _store.InitializeAsync(Ct);
        var id = await MoveAndRecordAsync(12, "<m12@example.com>");
        Gmail.Mails.Clear(); // ユーザーが別のフォルダーへ移した

        var outcome = await _undo.UndoAsync(id, asMisjudgement: true, Ct);

        Assert.Equal(UndoResult.NotFound, outcome.Result);
        Assert.Contains("何もしていません", outcome.Message);
        Assert.False((await _store.Judgements.GetAsync(id, Ct))?.IsCorrected);
    }

    [Fact]
    public async Task 戻せなかったときは_もう一度試せる()
    {
        await _store.InitializeAsync(Ct);
        var id = await MoveAndRecordAsync(13, "<m13@example.com>");
        Gmail.MoveFailure = new MailServerException("gmail1", "接続できません", isAuthenticationFailure: false);

        var failed = await _undo.UndoAsync(id, asMisjudgement: true, Ct);
        Gmail.MoveFailure = null;
        var retried = await _undo.UndoAsync(id, asMisjudgement: true, Ct);

        Assert.Equal(UndoResult.Failed, failed.Result);
        Assert.Equal(UndoResult.Restored, retried.Result);
    }

    [Fact]
    public async Task 二度目の戻しはしない()
    {
        await _store.InitializeAsync(Ct);
        var id = await MoveAndRecordAsync(14, "<m14@example.com>");
        await _undo.UndoAsync(id, asMisjudgement: true, Ct);

        var second = await _undo.UndoAsync(id, asMisjudgement: true, Ct);

        Assert.Contains("すでに", second.Message);
        Assert.Single(Gmail.Mails.Values, h => h.MessageId == "<m14@example.com>");
    }

    [Fact]
    public async Task お試しの見込みは_実測の平均が溜まるまで1通108秒で計算する()
    {
        await _store.InitializeAsync(Ct);
        AddSearchResults(5);

        var estimate = await _trials.EstimateAsync(Request(TrialMode.Preview, max: 3), Ct);

        Assert.Equal(5, estimate.Found);
        Assert.Equal(3, estimate.Selected);
        Assert.Equal(TimeSpan.FromSeconds(108 * 3), estimate.Total);
    }

    [Fact]
    public async Task お試しは上限で間引いて積み_受信トレイ以外は結果の表示のみにする()
    {
        await _store.InitializeAsync(Ct);
        AddSearchResults(10, folder: "Archive");

        var run = await _trials.StartAsync(Request(TrialMode.Move, max: 4, folder: "Archive"), Ct);

        Assert.Equal(TrialMode.Preview, run.Mode);
        Assert.Equal(4, run.Total);
        Assert.Equal(4, await _store.Queue.CountPendingForTrialAsync(run.Id, Ct));
        Assert.Equal(new TrialProgress(run.Id, 0, 4), _status.Current.Trial);
    }

    [Fact]
    public async Task 中止すると残りを処理待ちから消す()
    {
        await _store.InitializeAsync(Ct);
        AddSearchResults(3);
        var run = await _trials.StartAsync(Request(TrialMode.Preview, max: 10), Ct);

        await _trials.CancelAsync(run.Id, Ct);

        Assert.Equal(TrialRunStatus.Cancelled, (await _store.TrialRuns.GetAsync(run.Id, Ct))?.Status);
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
        Assert.Null(_status.Current.Trial);
    }

    [Fact]
    public async Task まとめて戻すと_その回で移動したメールだけを戻し_誤判定にはしない()
    {
        await _store.InitializeAsync(Ct);
        var run = await _store.TrialRuns.CreateAsync(new TrialRun
        {
            PeriodFrom = new DateOnly(2026, 10, 1), PeriodTo = new DateOnly(2026, 10, 15), Accounts = ["gmail1"],
            Folders = ["INBOX"], Mode = TrialMode.Move, MaxMessages = 10,
        }, [], Ct);
        var moved1 = await MoveAndRecordAsync(20, "<t20@example.com>", run.Id);
        var moved2 = await MoveAndRecordAsync(21, "<t21@example.com>", run.Id);
        var other = await MoveAndRecordAsync(22, "<n22@example.com>"); // 新着の移動は戻さない

        var summary = await _trials.RollbackAsync(run.Id, Ct);

        Assert.Equal(new RollbackSummary(2, 0, 0), summary);
        Assert.NotNull((await _store.TrialRuns.GetAsync(run.Id, Ct))?.RolledBackAt);
        Assert.False((await _store.Judgements.GetAsync(moved1, Ct))?.IsCorrected);
        Assert.NotNull((await _store.Judgements.GetAsync(moved2, Ct))?.Move?.UndoneAt);
        Assert.Null((await _store.Judgements.GetAsync(other, Ct))?.Move?.UndoneAt);
    }

    private TrialRequest Request(TrialMode mode, int max, string folder = "INBOX") =>
        new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15), ["gmail1"], folder, mode, max);

    private void AddSearchResults(int count, string folder = "INBOX")
    {
        for (var i = 0; i < count; i++)
        {
            Gmail.SearchResults.Add(new FoundMail(new MailRef("gmail1", folder, 777, (uint)(100 + i)), Now.AddDays(-10 + i)));
        }
    }

    /// <summary>メールを移動し、移動済みの判定履歴を作る。</summary>
    private async Task<long> MoveAndRecordAsync(uint uid, string messageId, long? trialRunId = null)
    {
        var mail = Gmail.Add(uid, "a@example.com", "件名", messageId, Now.AddDays(-1));
        var moved = await Gmail.MoveToItemFolderAsync(mail, new ItemId("important"), Ct);
        return await _store.Judgements.RecordAsync(new JudgementRecord
        {
            Mail = mail,
            MessageId = messageId,
            ReceivedAt = Now.AddDays(-1),
            Outcome = Outcome.Moved,
            Confidence = Confidence.High,
            TargetItemId = "important",
            TargetFolder = moved.ToFolder,
            TrialRunId = trialRunId,
            JudgedAt = Now,
        }, new MoveRecord { Moved = moved, MovedAt = Now }, null, Ct);
    }
}
