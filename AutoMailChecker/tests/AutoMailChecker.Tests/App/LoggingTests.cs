using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Logging;
using AutoMailChecker.Persistence;
using AutoMailChecker.Services;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Tests.App;

/// <summary>ログの方針（仕様書13章）: 本文は書かない。UID、件名の先頭20文字、判定結果、処理時間のみ。日付別に保存し、14日で削除する。</summary>
public sealed class LoggingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 処理のログには本文を書かず_件名は先頭20文字だけ()
    {
        var time = new ManualTimeProvider(Now);
        var store = new SqliteStateStore(_temp.Combine("state.db"), time);
        await store.InitializeAsync(Ct);
        var accounts = new FakeAccounts();
        var mailbox = accounts.Mailbox("gmail1");
        const string subject = "【重要】口座番号の変更手続きについてのお願いと期限のご案内です";
        var mail = mailbox.Add(1, "bank@example.com", subject, "<secret-body@example.com>", Now);
        await store.Queue.EnqueueAsync(mail, null, Ct);
        var logger = new ListLogger<MailProcessingService>();
        var config = new FixedConfig(DefaultConfig.Instance);
        var service = new MailProcessingService(
            store, accounts, new FakeClassifier(), new FakeRuntime(), new ActionPolicy(), new FakeRunModes(), new ProcessingGate(),
            config, new QueueSignal(), new AppStatusService(config), time, logger);

        await service.ProcessNextAsync(Ct);

        var all = string.Join("\n", logger.Messages);
        Assert.Contains("UID 1", all);
        Assert.Contains(subject[..20] + "…", all);
        Assert.DoesNotContain(subject, all);
        Assert.DoesNotContain("本文 secret-body", all); // 偽の読み取り口が返す本文
    }

    [Fact]
    public void ログは日付別のファイルに書き_14日より古いものは消す()
    {
        var directory = _temp.Combine("logs");
        Directory.CreateDirectory(directory);
        var old = Path.Combine(directory, "2026-09-30.log");
        var recent = Path.Combine(directory, "2026-10-05.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        var time = new ManualTimeProvider(Now);

        using (var provider = new FileLoggerProvider(directory, time))
        {
            provider.CreateLogger("AutoMailChecker.Services.Test").LogInformation("記録しました");
        }

        var today = Path.Combine(directory, $"{time.GetLocalNow():yyyy-MM-dd}.log");
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Contains("[INF] Test: 記録しました", File.ReadAllText(today));
    }
}
