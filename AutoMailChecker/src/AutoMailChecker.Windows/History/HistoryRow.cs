using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.Ui;

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

/// <summary>
/// 判定履歴の1行（画面設計 SC-05 の一覧と詳細）。表示用の文字列はここで作る。
/// 言語を切り替えたら読み直されるよう、文言は読むたびに作る。
/// </summary>
public sealed class HistoryRow(JudgementRecord record, IReadOnlyList<ItemDefinition> currentItems) : ObservableObject
{
    public JudgementRecord Record { get; } = record;
    public long Id => Record.Id;

    public string ReceivedText => (Record.ReceivedAt ?? Record.JudgedAt).ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string Account => Record.Mail.AccountId;
    public string Subject => Record.Subject ?? "";

    public string ItemsText => Record.Items.Count == 0
        ? "—"
        : string.Join(Translate.Text("、", ", "), Record.Items.Select(i => i.Label));

    public string ConfidenceText => Record.Confidence switch
    {
        Confidence.High => Translate.Text("高", "High"),
        Confidence.Medium => Translate.Text("中", "Medium"),
        Confidence.Low => Translate.Text("低", "Low"),
        _ => "—",
    };

    public string DestinationText => Describe(Record).Text;
    public HistoryStatus Status => Describe(Record).Status;

    public string FromText => Record.From ?? "";
    public string SummaryText => Record.Summary ?? "";
    public string ReasonText => Record.Reason ?? "";
    public string MessageIdText => Record.MessageId ?? Translate.Text("（なし）", "(none)");

    public string KindText => Record.TrialRunId is { } id
        ? Translate.Text($"お試し仕分け #{id}", $"Trial sort #{id}")
        : Translate.Text("新着", "New mail");

    public string CorrectionText => DescribeCorrection(Record, currentItems);

    /// <summary>「受信トレイに戻す」を押せるか（移動済みで、まだ戻していない行だけ）。</summary>
    public bool CanUndo => Record.Outcome is Outcome.Moved or Outcome.MovedAd && Record.Move is { UndoneAt: null };

    public static (string Text, HistoryStatus Status) Describe(JudgementRecord record)
    {
        var folder = record.Move?.Moved.ToFolder ?? record.TargetFolder ?? "";
        if (record.Move is { UndoneAt: not null } move)
        {
            return move.UndoResult == UndoResult.NotFound
                ? (Translate.Text($"戻せず（{folder} になかった）", $"Not moved back (not found in {folder})"), HistoryStatus.Undone)
                : (Translate.Text($"受信トレイに戻した（{folder}）", $"Moved back to inbox (from {folder})"), HistoryStatus.Undone);
        }

        return record.Outcome switch
        {
            Outcome.Moved or Outcome.MovedAd => (folder, HistoryStatus.Moved),
            Outcome.Planned => (Translate.Text($"予定: {folder}", $"Planned: {folder}"), HistoryStatus.Planned),
            Outcome.KeptAd => (Translate.Text("広告（受信トレイ）", "Ad (kept in inbox)"), HistoryStatus.Kept), // 以前の版の記録
            Outcome.KeptLowConfidence => (Translate.Text("確信度が低い（受信トレイ）", "Low confidence (kept in inbox)"), HistoryStatus.Kept),
            Outcome.KeptNoMatch => (Translate.Text("該当なし（受信トレイ）", "No match (kept in inbox)"), HistoryStatus.Kept),
            Outcome.KeptNoFolder => (Translate.Text("振り分け先なし（受信トレイ）", "No destination (kept in inbox)"), HistoryStatus.Kept),
            Outcome.Recorded => (Translate.Text("記録のみ", "Recorded only"), HistoryStatus.Kept),
            _ => (Translate.Text("失敗（受信トレイ）", "Failed (kept in inbox)"), HistoryStatus.Failed),
        };
    }

    private static string DescribeCorrection(JudgementRecord record, IReadOnlyList<ItemDefinition> items)
    {
        if (!record.IsCorrected)
        {
            return Translate.Text("記録なし", "Not recorded");
        }

        return record.CorrectItemId switch
        {
            null => Translate.Text("誤判定（本来はどの項目でもない）", "Misjudged (should match no item)"),
            ItemId.AdvertisementReserved => Translate.Text("誤判定（本来は広告）", "Misjudged (should be an ad)"),
            var id => Translate.Text($"誤判定（本来は「{LabelOf(items, id)}」）", $"Misjudged (should be \"{LabelOf(items, id)}\")"),
        };
    }

    private static string LabelOf(IReadOnlyList<ItemDefinition> items, string id) =>
        items.FirstOrDefault(i => i.Id.Value == id)?.Label ?? id;
}
