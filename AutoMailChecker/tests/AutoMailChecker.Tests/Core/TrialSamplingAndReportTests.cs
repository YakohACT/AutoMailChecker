using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;

namespace AutoMailChecker.Tests.Core;

public class TrialSamplingAndReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 上限以内ならすべてを受信日時順に選ぶ()
    {
        var found = new[] { Found("icloud", 3, 2), Found("gmail1", 1, 0), Found("gmail1", 2, 1) };

        var selected = TrialSampler.Select(found, 10);

        Assert.Equal([1u, 2u, 3u], selected.Select(f => f.Ref.Uid));
    }

    [Fact]
    public void 上限を超えたら_最初と最後を含めて等間隔に間引く()
    {
        var found = Enumerable.Range(0, 10).Select(i => Found("gmail1", (uint)i, i)).ToList();

        var selected = TrialSampler.Select(found, 4);

        Assert.Equal([0u, 3u, 6u, 9u], selected.Select(f => f.Ref.Uid));
    }

    [Fact]
    public void アカウントをまたいでも受信日時で間引く()
    {
        var found = Enumerable.Range(0, 6)
            .Select(i => Found(i % 2 == 0 ? "gmail1" : "icloud", (uint)i, i))
            .ToList();

        var selected = TrialSampler.Select(found, 3);

        Assert.Equal(["gmail1", "gmail1", "icloud"], selected.Select(f => f.Ref.AccountId));
    }

    [Fact]
    public void 結果には項目ごとの件数と確信度低_広告_失敗の件数を出す()
    {
        var judgements = new List<JudgementRecord>
        {
            Judged("important", Outcome.Planned),
            Judged("important", Outcome.Planned),
            Judged("payment", Outcome.Planned),
            Judged(null, Outcome.KeptLowConfidence),
            Judged(null, Outcome.KeptAd) with { IsAdvertisement = true },
            Judged(null, Outcome.KeptNoMatch),
            Judged(null, Outcome.Failed),
        };

        var report = TrialReportBuilder.Build(Run(), judgements, DefaultConfig.Instance.Items);

        Assert.Equal([("important", "重要", 2), ("payment", "決済履歴", 1)],
            report.ItemCounts.Select(c => (c.ItemId, c.Label, c.Count)));
        Assert.Equal(1, report.LowConfidence);
        Assert.Equal(1, report.Advertisement);
        Assert.Equal(1, report.NoMatch);
        Assert.Equal(1, report.Failed);
        Assert.Null(report.Accuracy); // 誤判定の記録がなければ出さない
    }

    [Fact]
    public void 誤判定の記録から_重要の見逃し率と誤振り分け率を出す()
    {
        var judgements = new List<JudgementRecord>
        {
            Judged("important", Outcome.Planned),                                                     // 正解（記録なし）
            Judged("important", Outcome.Planned),                                                     // 正解
            Judged("payment", Outcome.Planned) with { IsCorrected = true, CorrectItemId = "important" }, // 重要の見逃し・誤振り分け
            Judged(null, Outcome.KeptNoMatch) with { IsCorrected = true, CorrectItemId = "important" },  // 重要の見逃し
            Judged("reservation", Outcome.Planned) with { IsCorrected = true, CorrectItemId = null },   // 誤振り分け
        };

        var accuracy = TrialReportBuilder.Build(Run(), judgements, DefaultConfig.Instance.Items).Accuracy;

        Assert.NotNull(accuracy);
        Assert.Equal("重要", accuracy.KeyItemLabel);
        Assert.Equal(4, accuracy.KeyItemTotal);
        Assert.Equal(0.5, accuracy.MissRate);
        Assert.Equal(4, accuracy.RoutedTotal);
        Assert.Equal(0.5, accuracy.MisrouteRate);
        Assert.Equal(3, accuracy.Corrections);
    }

    private static FoundMail Found(string account, uint uid, int hours) =>
        new(new MailRef(account, "INBOX", 1, uid), Start.AddHours(hours));

    private static TrialRun Run() => new()
    {
        Id = 1, PeriodFrom = new DateOnly(2026, 10, 1), PeriodTo = new DateOnly(2026, 10, 15),
        Accounts = ["gmail1"], Folders = ["INBOX"], Mode = TrialMode.Preview, MaxMessages = 200, Total = 7,
    };

    private static JudgementRecord Judged(string? targetItemId, Outcome outcome) => new()
    {
        Mail = new MailRef("gmail1", "INBOX", 1, 1),
        Outcome = outcome,
        TargetItemId = targetItemId,
        TargetFolder = targetItemId is null ? null : $"AI/{targetItemId}",
        TrialRunId = 1,
        JudgedAt = Start,
    };
}
