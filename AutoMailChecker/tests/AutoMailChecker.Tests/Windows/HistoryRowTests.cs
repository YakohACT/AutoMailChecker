using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.History;
using AutoMailChecker.Windows.Trial;

namespace AutoMailChecker.Tests.Windows;

public class HistoryRowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Outcome.Planned, "予定: AI/重要", HistoryStatus.Planned)]
    [InlineData(Outcome.KeptAd, "広告（受信トレイ）", HistoryStatus.Kept)]
    [InlineData(Outcome.KeptLowConfidence, "確信度が低い（受信トレイ）", HistoryStatus.Kept)]
    [InlineData(Outcome.KeptNoMatch, "該当なし（受信トレイ）", HistoryStatus.Kept)]
    [InlineData(Outcome.KeptNoFolder, "振り分け先なし（受信トレイ）", HistoryStatus.Kept)]
    [InlineData(Outcome.Failed, "失敗（受信トレイ）", HistoryStatus.Failed)]
    public void 移動先の欄は結果に応じた文言と色の区分にする(Outcome outcome, string text, HistoryStatus status)
    {
        Assert.Equal((text, status), HistoryRow.Describe(Record(outcome)));
    }

    [Fact]
    public void 移動済みは移動先を出し_戻したものは戻したと出す()
    {
        var moved = Record(Outcome.Moved) with { Move = Move(undoneAt: null) };
        var undone = Record(Outcome.Moved) with { Move = Move(undoneAt: Now) with { UndoResult = UndoResult.Restored } };

        Assert.Equal(("AI/重要", HistoryStatus.Moved), HistoryRow.Describe(moved));
        Assert.Equal(("受信トレイに戻した（AI/重要）", HistoryStatus.Undone), HistoryRow.Describe(undone));
        Assert.True(new HistoryRow(moved, DefaultConfig.Instance.Items).CanUndo);
        Assert.False(new HistoryRow(undone, DefaultConfig.Instance.Items).CanUndo);
        Assert.False(new HistoryRow(Record(Outcome.Planned), DefaultConfig.Instance.Items).CanUndo);
    }

    [Fact]
    public void 誤判定の記録は本来の項目の表示名で出す()
    {
        var items = DefaultConfig.Instance.Items;

        Assert.Equal("記録なし", new HistoryRow(Record(Outcome.Planned), items).CorrectionText);
        Assert.Equal("誤判定（本来は「決済履歴」）",
            new HistoryRow(Record(Outcome.Planned) with { IsCorrected = true, CorrectItemId = "payment" }, items).CorrectionText);
        Assert.Equal("誤判定（本来はどの項目でもない）",
            new HistoryRow(Record(Outcome.Planned) with { IsCorrected = true }, items).CorrectionText);
    }

    [Theory]
    [InlineData(45, "約45分")]
    [InlineData(216, "約3時間36分")]
    [InlineData(360, "約6時間0分")]
    public void お試しの見込み時間の表示(int minutes, string expected)
    {
        Assert.Equal(expected, TrialViewModel.FormatDuration(TimeSpan.FromMinutes(minutes)));
    }

    private static JudgementRecord Record(Outcome outcome) => new()
    {
        Mail = new MailRef("gmail1", "INBOX", 1, 1),
        Outcome = outcome,
        TargetItemId = "important",
        TargetFolder = "AI/重要",
        JudgedAt = Now,
    };

    private static MoveRecord Move(DateTimeOffset? undoneAt) => new()
    {
        Id = 1,
        JudgementId = 1,
        Moved = new MovedMailRef("gmail1", "INBOX", "AI/重要", 5, 10),
        MovedAt = Now,
        UndoneAt = undoneAt,
    };
}
