using System.Diagnostics.CodeAnalysis;
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
    private readonly AppStatusService _status;
    private readonly MailProcessingService _service;

    public MailProcessingServiceTests()
    {
        _store = new SqliteStateStore(_temp.Combine("state.db"), _time);
        var config = new FixedConfig(DefaultConfig.Instance with { IgnoreSenders = ["noreply@example.com", "@ads.example.jp"] });
        _status = new AppStatusService(config);
        _service = new MailProcessingService(
            _store, _accounts, _classifier, _runtime, new ActionPolicy(), config, new QueueSignal(), _status, _time,
            NullLogger<MailProcessingService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AIで判定し_移動予定先と該当項目を記録する()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueWithHeaderAsync("gmail1", 10, "〇〇ホテル <info@hotel.example.com>", "ご予約確定のお知らせ", "<r1@hotel.example.com>");
        _classifier.Result = new Judgement(false, [new ItemId("payment"), new ItemId("reservation")], Confidence.High,
            "10月20日宿泊の予約確定", "宿泊予約の確定通知のため");

        Assert.True(await _service.ProcessNextAsync(Ct));

        var record = Assert.Single(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.Equal(Outcome.Planned, record.Outcome);   // 移動は開発ステップ4から
        Assert.Equal("reservation", record.TargetItemId); // 優先度は予約関連(2) ＞ 決済履歴(3)
        Assert.Equal("AI/予約関連", record.TargetFolder);
        Assert.Equal(Confidence.High, record.Confidence);
        Assert.Equal("10月20日宿泊の予約確定", record.Summary);
        Assert.Equal("ご予約確定のお知らせ", record.Subject);
        Assert.Equal("test-model", record.ModelName);
        Assert.Equal("p1", record.PromptVersion);
        Assert.Equal([("reservation", "予約関連", 2), ("payment", "決済履歴", 3)],
            record.Items.Select(i => (i.ItemId.Value, i.Label, i.Priority)));
        Assert.Equal("本文 r1@hotel.example.com", _classifier.LastMail?.BodyText);
        Assert.True(await _store.ProcessedMessages.ContainsAsync("gmail1", "<r1@hotel.example.com>", Ct));
        Assert.Equal(0, _status.Current.PendingCount);
    }

    [Theory]
    [InlineData(Confidence.Low, false, Outcome.KeptLowConfidence)]
    [InlineData(Confidence.High, true, Outcome.KeptAd)]
    public async Task 確信度が低いものと広告は受信トレイに残す予定にする(Confidence confidence, bool isAd, Outcome expected)
    {
        await _store.InitializeAsync(Ct);
        await EnqueueWithHeaderAsync("gmail1", 11, "a@example.com", "件名", "<m11@example.com>");
        _classifier.Result = new Judgement(isAd, [new ItemId("important")], confidence, "要約", "理由");

        await _service.ProcessNextAsync(Ct);

        var record = Assert.Single(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.Equal(expected, record.Outcome);
        Assert.Null(record.TargetFolder);
    }

    [Fact]
    public async Task 判定に失敗したら失敗として記録し_再判定しない()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueWithHeaderAsync("gmail1", 12, "a@example.com", "件名", "<m12@example.com>");
        _classifier.Failure = new ClassificationException("AI の出力が不正なため判定できませんでした");

        Assert.True(await _service.ProcessNextAsync(Ct));

        var record = Assert.Single(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.Equal(Outcome.Failed, record.Outcome);
        Assert.Null(record.Confidence);
        Assert.Contains("不正", record.Reason);
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task AIの準備ができるまでは取り出さない()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueWithHeaderAsync("gmail1", 13, "a@example.com", "件名", "<m13@example.com>");
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
        await EnqueueWithHeaderAsync("gmail1", 14, "a@example.com", "件名", "<m14@example.com>");
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
        await EnqueueWithHeaderAsync("gmail1", 15, from, "セールのお知らせ", "<ad1@example.com>");

        await _service.ProcessNextAsync(Ct);

        Assert.Equal(0, _classifier.Calls);
        Assert.Empty(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.True(await _store.ProcessedMessages.ContainsAsync("gmail1", "<ad1@example.com>", Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 処理済みと同じMessageIDのメールは二度判定しない()
    {
        await _store.InitializeAsync(Ct);
        await EnqueueWithHeaderAsync("gmail1", 16, "a@example.com", "件名", "<same@example.com>");
        await EnqueueWithHeaderAsync("gmail1", 30, "a@example.com", "件名", "<same@example.com>"); // 受信トレイに戻されて新しい UID が付いた

        await _service.ProcessNextAsync(Ct);
        await _service.ProcessNextAsync(Ct);

        Assert.Equal(1, _classifier.Calls);
        Assert.Single(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task サーバーにもうないメールは処理待ちから外す()
    {
        await _store.InitializeAsync(Ct);
        _accounts.Reader("gmail1");
        await _store.Queue.EnqueueAsync(Mail("gmail1", 17), null, Ct);

        Assert.True(await _service.ProcessNextAsync(Ct));

        Assert.Empty(await _store.Judgements.QueryAsync(new JudgementQuery(), Ct));
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 繋がらないアカウントは後回しにして_ほかのアカウントを先に処理する()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(Mail("gmail1", 1), null, Ct);
        await EnqueueWithHeaderAsync("icloud", 2, "a@example.com", "件名", "<i2@example.com>");
        _accounts.Reader("gmail1").Failure = new MailServerException("gmail1", "接続できません", isAuthenticationFailure: false);

        Assert.True(await _service.ProcessNextAsync(Ct));   // gmail1: 失敗して後回し
        Assert.True(await _service.ProcessNextAsync(Ct));   // icloud: 判定
        Assert.False(await _service.ProcessNextAsync(Ct));  // gmail1 は後回し中
        Assert.Equal(1, _status.Current.PendingCount);

        _time.Advance(MailProcessingService.AccountCooldown);
        _accounts.Reader("gmail1").Failure = null;
        Assert.True(await _service.ProcessNextAsync(Ct));   // 時間が経てばまた試す
    }

    [Fact]
    public async Task 想定外のエラーは上限まで再試行して諦める()
    {
        await _store.InitializeAsync(Ct);
        await _store.Queue.EnqueueAsync(Mail("gmail1", 18), null, Ct);
        _accounts.Reader("gmail1").Failure = new InvalidOperationException("想定外");

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
        await _store.Queue.EnqueueAsync(Mail("removed", 19), null, Ct);

        Assert.True(await _service.ProcessNextAsync(Ct));

        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    private static MailRef Mail(string account, uint uid) => new(account, "INBOX", 777, uid);

    private async Task EnqueueWithHeaderAsync(string account, uint uid, string from, string subject, string messageId)
    {
        var mail = Mail(account, uid);
        _accounts.Reader(account).Headers[uid] = new MailHeader(mail, messageId, from, subject, Now.AddMinutes(-5));
        await _store.Queue.EnqueueAsync(mail, null, Ct);
    }

    private sealed class FakeRuntime : ILlmRuntime
    {
        public LlmProfile? Running { get; set; } = LlmProfile.Normal;
        public string ModelName => "test-model";
        public Task StartAsync(LlmProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeClassifier : IMailClassifier
    {
        public Judgement Result { get; set; } = new(false, [], Confidence.High, "要約", "理由");
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public MailSnapshot? LastMail { get; private set; }

        public string PromptVersion => "p1";

        public Task<Judgement> ClassifyAsync(MailSnapshot mail, IReadOnlyList<ItemDefinition> items, CancellationToken ct)
        {
            Calls++;
            LastMail = mail;
            return Failure is not null ? throw Failure : Task.FromResult(Result);
        }
    }

    private sealed class FakeAccounts : IMailAccountRegistry
    {
        private readonly Dictionary<string, FakeReader> _readers = [];

        public IReadOnlyCollection<string> AccountIds => _readers.Keys;

        public FakeReader Reader(string accountId)
        {
            if (!_readers.TryGetValue(accountId, out var reader))
            {
                _readers[accountId] = reader = new FakeReader(accountId);
            }

            return reader;
        }

        public bool TryGetReader(string accountId, [NotNullWhen(true)] out IMailReader? reader)
        {
            reader = _readers.GetValueOrDefault(accountId);
            return reader is not null;
        }
    }

    private sealed class FakeReader(string accountId) : IMailReader
    {
        public Dictionary<uint, MailHeader> Headers { get; } = [];
        public Exception? Failure { get; set; }

        public string AccountId => accountId;

        public Task<MailHeader> FetchHeaderAsync(MailRef mail, CancellationToken ct) => Task.FromResult(Find(mail));

        public Task<MailSnapshot> FetchSnapshotAsync(MailRef mail, int maxBodyChars, CancellationToken ct)
        {
            var header = Find(mail);
            return Task.FromResult(new MailSnapshot(header, $"本文 {header.MessageId.Trim('<', '>')}", []));
        }

        public Task<IReadOnlyList<MailRef>> SearchByDateAsync(string folder, DateOnly from, DateOnly to, CancellationToken ct) =>
            throw new NotSupportedException();

        private MailHeader Find(MailRef mail)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            return Headers.TryGetValue(mail.Uid, out var header)
                ? header
                : throw new MailNotFoundException(mail, "メールがフォルダーにありません。");
        }
    }
}
