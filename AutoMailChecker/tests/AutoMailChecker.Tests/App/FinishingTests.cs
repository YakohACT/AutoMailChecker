using System.Xml.Linq;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Hosting;
using AutoMailChecker.Persistence;
using AutoMailChecker.Services;
using AutoMailChecker.Windows.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoMailChecker.Tests.App;

public sealed class FinishingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly FakeAccounts _accounts = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 振り分け先の候補は特別なフォルダーを除き_10分間使い回す()
    {
        var mailbox = _accounts.Mailbox("gmail1");
        var catalog = new MailFolderCatalog(_accounts, _time);

        var folders = await catalog.GetSortingFoldersAsync("gmail1", refresh: false, Ct);
        await catalog.GetSortingFoldersAsync("gmail1", refresh: false, Ct);
        _time.Advance(MailFolderCatalog.CacheDuration);
        await catalog.GetSortingFoldersAsync("gmail1", refresh: false, Ct);
        await catalog.GetSortingFoldersAsync("gmail1", refresh: true, Ct);

        Assert.DoesNotContain("INBOX", folders);
        Assert.DoesNotContain("[Gmail]", folders);
        Assert.DoesNotContain("[Gmail]/迷惑メール", folders);
        Assert.DoesNotContain("[Gmail]/送信済みメール", folders);
        Assert.Contains("AI/重要", folders);
        Assert.Equal(3, mailbox.ListFolderCalls);
    }

    [Fact]
    public async Task 試し判定は保存前の設定で判定し_移動も記録もしない()
    {
        var store = new SqliteStateStore(_temp.Combine("state.db"), _time);
        await store.InitializeAsync(Ct);
        var mailbox = _accounts.Mailbox("gmail1");
        var mail = mailbox.Add(10, "a@example.com", "件名", "<m10@example.com>", Now);
        var id = await store.Judgements.RecordAsync(new JudgementRecord
        {
            Mail = mail, MessageId = "<m10@example.com>", Outcome = Outcome.Planned, JudgedAt = Now,
        }, null, null, Ct);
        var classifier = new FakeClassifier { Result = new Judgement(false, [new ItemId("item_new")], Confidence.High, "要約", "理由") };
        var service = new RejudgeService(store, _accounts, classifier, new FakeRuntime(), new ActionPolicy(), _time);
        var draft = DefaultConfig.Instance with
        {
            Items =
            [
                .. DefaultConfig.Instance.Items,
                new ItemDefinition
                {
                    Id = new ItemId("item_new"), Label = "新しい項目", Priority = 5, Description = "説明",
                    Folders = new Dictionary<string, string> { ["gmail1"] = "AI/新しい項目" },
                },
            ],
        };

        var result = await service.RejudgeAsync(id, draft, Ct);

        Assert.Equal(["新しい項目"], result.MatchedLabels);
        Assert.Equal("AI/新しい項目", result.Destination);
        Assert.Empty(mailbox.Moves);
        Assert.Single(await store.Judgements.QueryAsync(new JudgementQuery(), Ct));
    }

    [Fact]
    public async Task 試し判定は移動済みのメールなら移動先から本文を取る()
    {
        var store = new SqliteStateStore(_temp.Combine("state.db"), _time);
        await store.InitializeAsync(Ct);
        var mailbox = _accounts.Mailbox("gmail1");
        var mail = mailbox.Add(11, "a@example.com", "件名", "<m11@example.com>", Now);
        var moved = await mailbox.MoveToItemFolderAsync(mail, new ItemId("important"), Ct);
        var id = await store.Judgements.RecordAsync(new JudgementRecord
        {
            Mail = mail, MessageId = "<m11@example.com>", Outcome = Outcome.Moved, JudgedAt = Now,
        }, new MoveRecord { Moved = moved, MovedAt = Now }, null, Ct);
        var classifier = new FakeClassifier();
        var service = new RejudgeService(store, _accounts, classifier, new FakeRuntime(), new ActionPolicy(), _time);

        await service.RejudgeAsync(id, DefaultConfig.Instance, Ct);

        Assert.Equal("AI/重要", classifier.LastMail?.Header.Ref.Folder);
    }

    [Fact]
    public void 完全停止中は試し判定できない()
    {
        var service = new RejudgeService(null!, _accounts, new FakeClassifier(), new FakeRuntime { Running = null }, new ActionPolicy(), _time);

        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void 自動起動のタスクは自分のログオンだけで_最小限の権限で_時間制限なしに動かす()
    {
        var xml = TaskSchedulerAutoStart.BuildTaskXml(@"C:\Apps\Auto & Mail\AutoMailChecker.exe", @"PC\haru0");

        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"PC\haru0", document.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")?.Value);
        Assert.Equal("LeastPrivilege", document.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", document.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("PT0S", document.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("IgnoreNew", document.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal(@"C:\Apps\Auto & Mail\AutoMailChecker.exe", document.Descendants(ns + "Command").Single().Value);
        Assert.Equal(@"C:\Apps\Auto & Mail", document.Descendants(ns + "WorkingDirectory").Single().Value);
    }

    [Fact]
    public void 起動時に組み立てるサービスと設定画面が使うサービスをすべて作れる()
    {
        using var host = AppHost.Build(new AppPaths(_temp.Combine("config"), _temp.Combine("data")));
        var services = host.Services;

        // 起動せずに作るだけ（llama-server や IMAP には繋がない）
        Assert.Equal(7, services.GetServices<IHostedService>().Count());
        Assert.NotNull(services.GetRequiredService<IMailFolderCatalog>());
        Assert.NotNull(services.GetRequiredService<IMailConnectionTester>());
        Assert.NotNull(services.GetRequiredService<IRejudgeService>());
        Assert.NotNull(services.GetRequiredService<IAutoStartRegistrar>());
        Assert.NotNull(services.GetRequiredService<IUndoService>());
        Assert.NotNull(services.GetRequiredService<ITrialService>());
        Assert.NotNull(services.GetRequiredService<IRunModeController>());
        Assert.NotNull(services.GetRequiredService<ICredentialStore>());
    }
}
