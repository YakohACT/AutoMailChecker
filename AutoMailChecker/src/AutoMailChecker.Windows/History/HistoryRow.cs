using AutoMailChecker.Core;
using AutoMailChecker.Core.State;

namespace AutoMailChecker.Windows.History;

/// <summary>判定履歴の1行の状態。「移動先」の文字色に使う。</summary>
public enum HistoryStatus
{
    Moved,
    Planned,
    Kept,
    Failed,
    Undone,
}

/// <summary>判定履歴の1行（画面設計 SC-05 の一覧と詳細）。表示用の文字列はここで作る。</summary>
public sealed class HistoryRow
{
    public HistoryRow(JudgementRecord record, IReadOnlyList<ItemDefinition> currentItems)
    {
        Record = record;
        (DestinationText, Status) = Describe(record);
        CorrectionText = DescribeCorrection(record, currentItems);
    }

    public JudgementRecord Record { get; }
    public long Id => Record.Id;

    public string ReceivedText => (Record.ReceivedAt ?? Record.JudgedAt).ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string Account => Record.Mail.AccountId;
    public string Subject => Record.Subject ?? "";
    public string ItemsText => Record.Items.Count == 0 ? "—" : string.Join("、", Record.Items.Select(i => i.Label));

    public string ConfidenceText => Record.Confidence switch
    {
        Confidence.High => "高",
        Confidence.Medium => "中",
        Confidence.Low => "低",
        _ => "—",
    };

    public string DestinationText { get; }
    public HistoryStatus Status { get; }

    public string FromText => Record.From ?? "";
    public string SummaryText => Record.Summary ?? "";
    public string ReasonText => Record.Reason ?? "";
    public string MessageIdText => Record.MessageId ?? "（なし）";
    public string KindText => Record.TrialRunId is { } id ? $"お試し仕分け #{id}" : "新着";
    public string CorrectionText { get; }

    /// <summary>「受信トレイに戻す」を押せるか（移動済みで、まだ戻していない行だけ）。</summary>
    public bool CanUndo => Record.Outcome is Outcome.Moved or Outcome.MovedAd && Record.Move is { UndoneAt: null };

    public static (string Text, HistoryStatus Status) Describe(JudgementRecord record)
    {
        var folder = record.Move?.Moved.ToFolder ?? record.TargetFolder ?? "";
        if (record.Move is { UndoneAt: not null } move)
        {
            return move.UndoResult == UndoResult.NotFound
                ? ($"戻せず（{folder} になかった）", HistoryStatus.Undone)
                : ($"受信トレイに戻した（{folder}）", HistoryStatus.Undone);
        }

        return record.Outcome switch
        {
            Outcome.Moved or Outcome.MovedAd => (folder, HistoryStatus.Moved),
            Outcome.Planned => ($"予定: {folder}", HistoryStatus.Planned),
            Outcome.KeptAd => ("広告（受信トレイ）", HistoryStatus.Kept),
            Outcome.KeptLowConfidence => ("確信度が低い（受信トレイ）", HistoryStatus.Kept),
            Outcome.KeptNoMatch => ("該当なし（受信トレイ）", HistoryStatus.Kept),
            Outcome.KeptNoFolder => ("振り分け先なし（受信トレイ）", HistoryStatus.Kept),
            Outcome.Recorded => ("記録のみ", HistoryStatus.Kept),
            _ => ("失敗（受信トレイ）", HistoryStatus.Failed),
        };
    }

    private static string DescribeCorrection(JudgementRecord record, IReadOnlyList<ItemDefinition> items)
    {
        if (!record.IsCorrected)
        {
            return "記録なし";
        }

        return record.CorrectItemId switch
        {
            null => "誤判定（本来はどの項目でもない）",
            ItemId.AdvertisementReserved => "誤判定（本来は広告）",
            var id => $"誤判定（本来は「{items.FirstOrDefault(i => i.Id.Value == id)?.Label ?? id}」）",
        };
    }
}
