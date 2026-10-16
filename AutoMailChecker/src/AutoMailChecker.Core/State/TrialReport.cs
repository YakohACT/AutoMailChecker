namespace AutoMailChecker.Core.State;

/// <summary>お試し仕分けの1回分の結果（画面設計 SC-06「完了」）。</summary>
/// <param name="ItemCounts">項目ごとの移動（予定）件数。優先度順</param>
/// <param name="AdvertisementFolder">広告用フォルダーへの移動（予定）件数</param>
/// <param name="Accuracy">誤判定が1件以上記録されていれば出す</param>
public sealed record TrialReport(
    TrialRun Run,
    IReadOnlyList<TrialItemCount> ItemCounts,
    int AdvertisementFolder,
    int LowConfidence,
    int Advertisement,
    int NoMatch,
    int Failed,
    int Moved,
    TrialAccuracy? Accuracy);

public sealed record TrialItemCount(string ItemId, string Label, int Count);

/// <summary>この回の精度（テスト設計「判定精度の評価」）。誤判定の記録がないものは正解とみなす。</summary>
/// <param name="KeyItemLabel">見逃し率を出す項目（既定は「重要」）</param>
/// <param name="MissRate">正解が KeyItem のうち、KeyItem への移動（予定）にならなかった割合</param>
/// <param name="MisrouteRate">移動（予定）になったもののうち、移動先が正解と違う割合</param>
public sealed record TrialAccuracy(
    string KeyItemLabel, double? MissRate, int KeyItemTotal, double? MisrouteRate, int RoutedTotal, int Corrections);

public static class TrialReportBuilder
{
    /// <summary>見逃し率を出す項目の ID。この ID がなければ、優先度が最も高い項目を使う。</summary>
    public const string DefaultKeyItemId = "important";

    public static TrialReport Build(TrialRun run, IReadOnlyList<JudgementRecord> judgements, IReadOnlyList<ItemDefinition> items)
    {
        var routed = judgements.Where(j => j.TargetItemId is not null && j.Outcome is Outcome.Planned or Outcome.Moved).ToList();
        var itemCounts = routed
            .GroupBy(j => j.TargetItemId!)
            .Select(g => new TrialItemCount(g.Key, LabelOf(g.Key, items, g), g.Count()))
            .OrderBy(c => items.FirstOrDefault(i => i.Id.Value == c.ItemId)?.Priority ?? int.MaxValue)
            .ThenBy(c => c.ItemId, StringComparer.Ordinal)
            .ToList();

        return new TrialReport(
            run,
            itemCounts,
            AdvertisementFolder: judgements.Count(j => Predicted(j) == ItemId.AdvertisementReserved),
            LowConfidence: judgements.Count(j => j.Outcome == Outcome.KeptLowConfidence),
            Advertisement: judgements.Count(j => j.IsAdvertisement),
            NoMatch: judgements.Count(j => j.Outcome == Outcome.KeptNoMatch),
            Failed: judgements.Count(j => j.Outcome == Outcome.Failed),
            Moved: judgements.Count(j => j.Outcome is Outcome.Moved or Outcome.MovedAd && j.Move?.UndoneAt is null),
            Accuracy: judgements.Any(j => j.IsCorrected) ? ComputeAccuracy(judgements, items) : null);
    }

    private static TrialAccuracy ComputeAccuracy(IReadOnlyList<JudgementRecord> judgements, IReadOnlyList<ItemDefinition> items)
    {
        var key = items.FirstOrDefault(i => i.Id.Value == DefaultKeyItemId)
            ?? items.Where(i => i.Enabled).OrderBy(i => i.Priority).FirstOrDefault();
        var keyId = key?.Id.Value;

        var scored = judgements
            .Where(j => j.Outcome != Outcome.Failed)
            .Select(j => (Predicted: Predicted(j), Correct: j.IsCorrected ? j.CorrectItemId : Predicted(j)))
            .ToList();

        var keyMails = scored.Where(s => keyId is not null && s.Correct == keyId).ToList();
        var routed = scored.Where(s => s.Predicted is not null).ToList();

        return new TrialAccuracy(
            key?.Label ?? "",
            Rate(keyMails.Count(s => s.Predicted != keyId), keyMails.Count),
            keyMails.Count,
            Rate(routed.Count(s => s.Correct != s.Predicted), routed.Count),
            routed.Count,
            judgements.Count(j => j.IsCorrected));
    }

    /// <summary>AI の判定から決まった振り分け先。項目なら項目 ID、広告用フォルダーなら「ad」、移動しないなら null。</summary>
    public static string? Predicted(JudgementRecord judgement) =>
        judgement.TargetItemId
        ?? (judgement.TargetFolder is not null && judgement.IsAdvertisement ? ItemId.AdvertisementReserved : null);

    private static string LabelOf(string itemId, IReadOnlyList<ItemDefinition> items, IEnumerable<JudgementRecord> group) =>
        items.FirstOrDefault(i => i.Id.Value == itemId)?.Label
        ?? group.SelectMany(j => j.Items).FirstOrDefault(i => i.ItemId.Value == itemId)?.Label
        ?? itemId;

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;
}
