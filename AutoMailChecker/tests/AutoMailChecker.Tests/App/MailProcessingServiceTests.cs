using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Persistence;
using AutoMailChecker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.App;

public sealed class MailProcessingServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly SqliteStateStore _store;
    private readonly FakeAccounts _accounts = new();
    private readonly FakeClassifier _classifier = new();
    private readonly FakeRuntime _runtime = new();
    private AppStatusService _status = null!;
    private MailProcessingService _service = null!;

    public MailProcessingServiceTests()
    {
        _store = new SqliteStateStore(_temp.Combine("state.db"), _time);
        UseConfig(autoSort: false);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeMailbox Gmail => _accounts.Mailbox("gmail1");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 自動仕分けがオフなら_移動予定先と該当項目を記録するだけ()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 10, "〇〇ホテル <info@hotel.example.com>", "ご予約確定のお知らせ", "<r1@hotel.example.com>");
        _classifier.Result = Judge(["payment", "reservation"]);

        Assert.True(await _service.ProcessNextAsync(Ct));

        var record = await SingleRecordAsync();
        Assert.Equal(Outcome.Planned, record.Outcome);
        Assert.Equal("reservation", record.TargetItemId); // 優先度は予約関連(2) ＞ 決済履歴(3)
        Assert.Equal("AI/予約関連", record.TargetFolder);
        Assert.Null(record.Move);
        Assert.Empty(Gmail.Moves);
        Assert.Equal("test-model", record.ModelName);
        Assert.Equal("p1", record.PromptVersion);
        Assert.Equal([("reservation", "予約関連", 2), ("payment", "決済履歴", 3)],
            record.Items.Select(i => (i.ItemId.Value, i.Label, i.Priority)));
        Assert.Equal("本文 r1@hotel.example.com", _classifier.LastMail?.BodyText);
        Assert.True(await _store.ProcessedMessages.ContainsAsync("gmail1", "<r1@hotel.example.com>", Ct));
        Assert.Equal(0, _status.Current.PendingCount);
    }

    [Fact]
    public async Task 自動仕分けがオンなら_優先度が最も高い項目のフォルダーへ移動して記録する()
    {
        UseConfig(autoSort: true);
        await _store.InitializeAsync(Ct);
        var mail = await EnqueueAsync(Gmail, 11, "a@example.com", "件名", "<m11@example.com>");
        _classifier.Result = Judge(["payment", "important"]);

        await _service.ProcessNextAsync(Ct);

        var record = await SingleRecordAsync();
        Assert.Equal(Outcome.Moved, record.Outcome);
        Assert.Equal("AI/重要", record.TargetFolder);
        Assert.Equal((mail, "AI/重要"), Assert.Single(Gmail.Moves));
        Assert.Equal("AI/重要", record.Move?.Moved.ToFolder);
        Assert.NotNull(record.Move?.Moved.DestUid);
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 移動を許さないアカウントでは移動しない()
    {
        UseConfig(autoSort: true, allowMove: false);
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 12, "a@example.com", "件名", "<m12@example.com>");
        _classifier.Result = Judge(["important"]);

        await _service.ProcessNextAsync(Ct);

        Assert.Equal(Outcome.Planned, (await SingleRecordAsync()).Outcome);
        Assert.Empty(Gmail.Moves);
    }

    [Fact]
    public async Task 広告は振り分けの設定なら広告用フォルダーへ移動する()
    {
        UseConfig(autoSort: true, advertisement: AdvertisementMode.Move);
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 13, "sale@shop.example.com", "セール", "<ad@example.com>");
        _classifier.Result = Judge(["payment"], isAd: true);

        await _service.ProcessNextAsync(Ct);

        var record = await SingleRecordAsync();
        Assert.Equal(Outcome.MovedAd, record.Outcome);
        Assert.Equal("AI/広告", record.Move?.Moved.ToFolder);
    }

    [Fact]
    public async Task 振り分け先がサーバーになければ受信トレイに残して知らせ_次からは次点の項目へ移動する()
    {
        UseConfig(autoSort: true);
        await _store.InitializeAsync(Ct);
        Gmail.Folders.Remove("AI/重要");
        await EnqueueAsync(Gmail, 14, "a@example.com", "件名1", "<m14@example.com>");
        await EnqueueAsync(Gmail, 15, "a@example.com", "件名2", "<m15@example.com>");
        _classifier.Result = Judge(["important", "payment"]);

        await _service.ProcessNextAsync(Ct);
        await _service.ProcessNextAsync(Ct);

        var records = await _store.Judgements.QueryAsync(new JudgementQuery(), Ct);
        var first = records.Single(r => r.Mail.Uid == 14);
        var second = records.Single(r => r.Mail.Uid == 15);
        Assert.Equal(Outcome.KeptNoFolder, first.Outcome);
        Assert.Null(first.Move);
        Assert.Equal(Outcome.Moved, second.Outcome);
        Assert.Equal("AI/決済履歴", second.Move?.Moved.ToFolder);
        Assert.Contains(_status.Current.Errors, e => e.Source == MailProcessingService.MoveErrorSource && e.Message.Contains("AI/重要"));
    }

    [Fact]
    public async Task 移動に失敗したら受信トレイに残し_失敗を明記して知らせる()
    {
        UseConfig(autoSort: true);
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 16, "a@example.com", "件名", "<m16@example.com>");
        _classifier.Result = Judge(["important"]);
        Gmail.MoveFailure = new MailMoveException("サーバーが MOVE にも UIDPLUS にも対応していないため、移動できません。");

        await _service.ProcessNextAsync(Ct);

        var record = await SingleRecordAsync();
        Assert.Equal(Outcome.Failed, record.Outcome);
        Assert.Contains("移動に失敗しました", record.Reason);
        Assert.Contains("UIDPLUS", record.Reason);
        Assert.Contains(_status.Current.Errors, e => e.Source == MailProcessingService.MoveErrorSource);
    }

    [Fact]
    public async Task 移動の直後に落ちても_再起動後に二重に移動せず履歴を補う()
    {
        UseConfig(autoSort: true);
        await _store.InitializeAsync(Ct);
        var mail = await EnqueueAsync(Gmail, 17, "a@example.com", "件名", "<m17@example.com>");
        _classifier.Result = Judge(["important"]);

        // 移動の直前に控えを残したところで落ち、サーバー側では移動が済んでいた状態を作る
        var item = await _store.Queue.TakeNextAsync(Ct);
        var pending = PendingMove.Create(Record(mail, Outcome.Moved, "important", "AI/重要"), new ItemTarget(new ItemId("important"), "AI/重要"));
        await _store.Queue.SavePendingMoveAsync(item!.Id, pending.ToJson(), Ct);
        await Gmail.MoveToItemFolderAsync(mail, new ItemId("important"), Ct);
        await new SqliteStateStore(_temp.Combine("state.db"), _time).InitializeAsync(Ct); // 再起動
        var callsBefore = _classifier.Calls;

        await _service.ProcessNextAsync(Ct);

        var record = await SingleRecordAsync();
        Assert.Equal(Outcome.Moved, record.Outcome);
        Assert.Equal("AI/重要", record.Move?.Moved.ToFolder);
        Assert.Single(Gmail.Moves);                     // 二重に移動しない
        Assert.Equal(callsBefore, _classifier.Calls);   // 判定もし直さない
    }

    [Fact]
    public async Task 移動の直前で落ちていたら_再起動後に移動し直す()
    {
        UseConfig(autoSort: true);
        await _store.InitializeAsync(Ct);
        var mail = await EnqueueAsync(Gmail, 18, "a@example.com", "件名", "<m18@example.com>");
        var item = await _store.Queue.TakeNextAsync(Ct);
        var pending = PendingMove.Create(Record(mail, Outcome.Moved, "payment", "AI/決済履歴"), new ItemTarget(new ItemId("payment"), "AI/決済履歴"));
        await _store.Queue.SavePendingMoveAsync(item!.Id, pending.ToJson(), Ct);
        await new SqliteStateStore(_temp.Combine("state.db"), _time).InitializeAsync(Ct); // 再起動

        await _service.ProcessNextAsync(Ct);

        Assert.Equal("AI/決済履歴", (await SingleRecordAsync()).Move?.Moved.ToFolder);
        Assert.Single(Gmail.Moves);
        Assert.Equal(0, _classifier.Calls);
    }

    [Theory]
    [InlineData(Confidence.Low, false, Outcome.KeptLowConfidence)]
    [InlineData(Confidence.High, true, Outcome.KeptAd)]
    public async Task 確信度が低いものと広告は受信トレイに残す(Confidence confidence, bool isAd, Outcome expected)
    {
        UseConfig(autoSort: true);
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 19, "a@example.com", "件名", "<m19@example.com>");
        _classifier.Result = Judge(["important"], confidence, isAd);

        await _service.ProcessNextAsync(Ct);

        var record = await SingleRecordAsync();
        Assert.Equal(expected, record.Outcome);
        Assert.Null(record.TargetFolder);
        Assert.Empty(Gmail.Moves);
    }

    [Fact]
    public async Task 判定に失敗したら失敗として記録し_再判定しない()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 20, "a@example.com", "件名", "<m20@example.com>");
        _classifier.Failure = new ClassificationException("AI の出力が不正なため判定できませんでした");

        Assert.True(await _service.ProcessNextAsync(Ct));

        var record = await SingleRecordAsync();
        Assert.Equal(Outcome.Failed, record.Outcome);
        Assert.Null(record.Confidence);
        Assert.Contains("不正", record.Reason);
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task お試しは結果の表示のみなら移動せず_実際に移動する回なら移動し_終わったら回を閉じる()
    {
        UseConfig(autoSort: false); // 新着の自動仕分けがオフでも、お試しの「実際に移動する」は移動する
        await _store.InitializeAsync(Ct);
        var preview = Gmail.Add(30, "a@example.com", "件名", "<t30@example.com>", Now.AddDays(-3));
        var moving = Gmail.Add(31, "a@example.com", "件名", "<t31@example.com>", Now.AddDays(-3));
        var previewRun = await CreateTrialAsync(TrialMode.Preview, preview);
        var moveRun = await CreateTrialAsync(TrialMode.Move, moving);
        _classifier.Result = Judge(["important"]);
        _status.SetTrial(new TrialProgress(moveRun.Id, 0, 1));

        await _service.ProcessNextAsync(Ct);
        await _service.ProcessNextAsync(Ct);

        var records = await _store.Judgements.QueryAsync(new JudgementQuery(), Ct);
        Assert.Equal(Outcome.Planned, records.Single(r => r.TrialRunId == previewRun.Id).Outcome);
        Assert.Equal(Outcome.Moved, records.Single(r => r.TrialRunId == moveRun.Id).Outcome);
        Assert.Equal(TrialRunStatus.Done, (await _store.TrialRuns.GetAsync(previewRun.Id, Ct))?.Status);
        Assert.Equal(TrialRunStatus.Done, (await _store.TrialRuns.GetAsync(moveRun.Id, Ct))?.Status);
        Assert.Null(_status.Current.Trial);
        Assert.False(await _store.ProcessedMessages.ContainsAsync("gmail1", "<t31@example.com>", Ct)); // お試しは新着と独立
    }

    [Fact]
    public async Task AIの準備ができるまでは取り出さない()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 21, "a@example.com", "件名", "<m21@example.com>");
        _runtime.Running = null;

        Assert.False(await _service.ProcessNextAsync(Ct));
        Assert.Equal(1, _status.Current.PendingCount);

        _runtime.Running = LlmProfile.Normal;
        Assert.True(await _service.ProcessNextAsync(Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task llama_serverに繋がらなければ処理待ちに戻して後で判定する()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 22, "a@example.com", "件名", "<m22@example.com>");
        _classifier.Failure = new LlmUnavailableException("llama-server に接続できません");

        Assert.True(await _service.ProcessNextAsync(Ct));
        Assert.Empty(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.False(await _service.ProcessNextAsync(Ct)); // しばらく待つ

        _time.Advance(MailProcessingService.LlmCooldown);
        _classifier.Failure = null;
        Assert.True(await _service.ProcessNextAsync(Ct));
        Assert.Single(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
    }

    [Theory]
    [InlineData("NoReply@example.com")]
    [InlineData("広告 <sale@ads.example.jp>")]
    public async Task 除外リストの差出人はAIに渡さず_処理済みにだけ残す(string from)
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 23, from, "セールのお知らせ", "<ad1@example.com>");

        await _service.ProcessNextAsync(Ct);

        Assert.Equal(0, _classifier.Calls);
        Assert.Empty(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.True(await _store.ProcessedMessages.ContainsAsync("gmail1", "<ad1@example.com>", Ct));
    }

    [Fact]
    public async Task 処理済みと同じMessageIDのメールは二度判定しない()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 24, "a@example.com", "件名", "<same@example.com>");
        await EnqueueAsync(Gmail, 40, "a@example.com", "件名", "<same@example.com>"); // 受信トレイに戻されて新しい UID が付いた

        await _service.ProcessNextAsync(Ct);
        await _service.ProcessNextAsync(Ct);

        Assert.Equal(1, _classifier.Calls);
        Assert.Single(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
    }

    [Fact]
    public async Task サーバーにもうないメールは処理待ちから外す()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(new MailRef("gmail1", "INBOX", 777, 25), null, Ct);
        _ = Gmail;

        Assert.True(await _service.ProcessNextAsync(Ct));

        Assert.Empty(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 繋がらないアカウントは後回しにして_ほかのアカウントを先に処理する()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 1, "a@example.com", "件名", "<g1@example.com>");
        await EnqueueAsync(_accounts.Mailbox("icloud"), 2, "a@example.com", "件名", "<i2@example.com>");
        Gmail.ReadFailure = new MailServerException("gmail1", "接続できません", isAuthenticationFailure: false);

        Assert.True(await _service.ProcessNextAsync(Ct));   // gmail1: 失敗して後回し
        Assert.True(await _service.ProcessNextAsync(Ct));   // icloud: 判定
        Assert.False(await _service.ProcessNextAsync(Ct));  // gmail1 は後回し中
        Assert.Equal(1, _status.Current.PendingCount);

        _time.Advance(MailProcessingService.AccountCooldown);
        Gmail.ReadFailure = null;
        Assert.True(await _service.ProcessNextAsync(Ct));   // 時間が経てばまた試す
        Assert.Equal(2, (await _store.Judgements.QueryAsync(new JudgementQuery(), Ct)).Count);
    }

    [Fact]
    public async Task 想定外のエラーは上限まで再試行して諦める()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueAsync(Gmail, 26, "a@example.com", "件名", "<m26@example.com>");
        Gmail.ReadFailure = new InvalidOperationException("想定外");

        for (var i = 0; i < MailProcessingService.MaxAttempts; i++)
        {
            Assert.True(await _service.ProcessNextAsync(Ct));
        }

        Assert.False(await _service.ProcessNextAsync(Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 設定にないアカウントの分は処理待ちから外す()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(new MailRef("removed", "INBOX", 777, 27), null, Ct);

        Assert.True(await _service.ProcessNextAsync(Ct));

        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    private void UseConfig(bool autoSort, bool allowMove = true, AdvertisementMode advertisement = AdvertisementMode.Ignore)
    {
        var defaults = DefaultConfig.Instance;
        var config = new FixedConfig(defaults with
        {
            Accounts = [.. defaults.Accounts.Select(a => a with { AllowMove = allowMove })],
            IgnoreSenders = ["noreply@example.com", "@ads.example.jp"],
            Policy = defaults.Policy with { AutoSort = autoSort },
            Advertisement = defaults.Advertisement with { Mode = advertisement },
        });
        _status = new AppStatusService(config);
        _service = new MailProcessingService(
            _store, _accounts, _classifier, _runtime, new ActionPolicy(), config, new QueueSignal(), _status, _time,
            NullLogger<MailProcessingService>.Instance);
    }

    private async Task<MailRef> EnqueueAsync(FakeMailbox mailbox, uint uid, string from, string subject, string messageId)
    {
        var mail = mailbox.Add(uid, from, subject, messageId, Now.AddMinutes(-5));
        await _store.Queue.EnqueueAsync(mail, null, Ct);
        return mail;
    }

    private Task<TrialRun> CreateTrialAsync(TrialMode mode, MailRef target) =>
        _store.TrialRuns.CreateAsync(new TrialRun
        {
            PeriodFrom = new DateOnly(2026, 10, 1),
            PeriodTo = new DateOnly(2026, 10, 15),
            Accounts = ["gmail1"],
            Folders = ["INBOX"],
            Mode = mode,
            MaxMessages = 200,
        }, [target], Ct);

    private async Task<JudgementRecord> SingleRecordAsync() =>
        Assert.Single(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));

    private static Judgement Judge(string[] items, Confidence confidence = Confidence.High, bool isAd = false) =>
        new(isAd, items.Select(i => new ItemId(i)).ToList(), confidence, "要約", "理由");

    private static JudgementRecord Record(MailRef mail, Outcome outcome, string itemId, string folder) => new()
    {
        Mail = mail,
        MessageId = $"<m{mail.Uid}@example.com>",
        Subject = "件名",
        Confidence = Confidence.High,
        Outcome = outcome,
        TargetItemId = itemId,
        TargetFolder = folder,
        JudgedAt = Now,
    };
}
