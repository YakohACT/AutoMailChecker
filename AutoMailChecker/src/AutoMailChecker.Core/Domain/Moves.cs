namespace AutoMailChecker.Core;

/// <summary>判定履歴に残す結果の値（E-R 図の outcome）。</summary>
public enum Outcome
{
    /// <summary>項目のフォルダーへ移動した</summary>
    Moved,
    /// <summary>広告用フォルダーへ移動した（以前の版の記録だけ。いまは広告かどうかを判定しない）</summary>
    MovedAd,
    /// <summary>お試し（結果の表示のみ）または移動不可のアカウントで、移動予定先だけ決めた</summary>
    Planned,
    /// <summary>広告なので受信トレイに残した（以前の版の記録だけ。いまは広告かどうかを判定しない）</summary>
    KeptAd,
    /// <summary>確信度がしきい値未満なので残した</summary>
    KeptLowConfidence,
    /// <summary>該当項目がないので残した</summary>
    KeptNoMatch,
    /// <summary>該当項目の振り分け先が未設定またはサーバーにないので残した</summary>
    KeptNoFolder,
    /// <summary>判定または移動に失敗した</summary>
    Failed,
    /// <summary>
    /// 件名と差出人だけを記録した（AI 判定なし）。開発ステップ2（監視と記録）で使う。
    /// E-R 図の値には無く、このアプリで足したもの。
    /// </summary>
    Recorded,
}

public static class OutcomeExtensions
{
    public static string ToDbValue(this Outcome value) => value switch
    {
        Outcome.Moved => "moved",
        Outcome.MovedAd => "moved_ad",
        Outcome.Planned => "planned",
        Outcome.KeptAd => "kept_ad",
        Outcome.KeptLowConfidence => "kept_low_confidence",
        Outcome.KeptNoMatch => "kept_no_match",
        Outcome.KeptNoFolder => "kept_no_folder",
        Outcome.Failed => "failed",
        Outcome.Recorded => "recorded",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static Outcome ParseOutcome(string value) => value switch
    {
        "moved" => Outcome.Moved,
        "moved_ad" => Outcome.MovedAd,
        "planned" => Outcome.Planned,
        "kept_ad" => Outcome.KeptAd,
        "kept_low_confidence" => Outcome.KeptLowConfidence,
        "kept_no_match" => Outcome.KeptNoMatch,
        "kept_no_folder" => Outcome.KeptNoFolder,
        "failed" => Outcome.Failed,
        "recorded" => Outcome.Recorded,
        _ => throw new FormatException($"不明な結果の値です: {value}"),
    };
}

/// <summary>
/// ActionPolicy が選んだ移動先。Folder は判定履歴に残すための控えで、
/// 実際の移動先は IItemFolderMover が設定から引き直す（外から移動先を指定する手段を作らないため）。
/// </summary>
public abstract record MoveTarget(string Folder);

public sealed record ItemTarget(ItemId Item, string Folder) : MoveTarget(Folder);

/// <summary>ActionPolicy の結果。Target が null なら移動しない。</summary>
public sealed record MoveDecision(MoveTarget? Target, Outcome Outcome)
{
    /// <summary>実際に IMAP で移動するか。Planned は移動予定先を残すだけで移動しない。</summary>
    public bool ShouldMove => Target is not null && Outcome == Outcome.Moved;
}

/// <summary>
/// 移動後の位置。「元に戻す」に使う。
/// DestUid はサーバーが COPYUID（UIDPLUS 拡張）を返したときだけ入る。
/// </summary>
public sealed record MovedMailRef(
    string AccountId,
    string FromFolder,
    string ToFolder,
    uint? DestUidValidity,
    uint? DestUid);

/// <summary>「受信トレイに戻す」の結果。</summary>
public enum UndoResult
{
    /// <summary>受信トレイに戻した</summary>
    Restored,
    /// <summary>すでに別の場所へ移されていたので何もしなかった</summary>
    NotFound,
    /// <summary>失敗した</summary>
    Failed,
}

public static class UndoResultExtensions
{
    public static string ToDbValue(this UndoResult value) => value switch
    {
        UndoResult.Restored => "restored",
        UndoResult.NotFound => "not_found",
        UndoResult.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static UndoResult ParseUndoResult(string value) => value switch
    {
        "restored" => UndoResult.Restored,
        "not_found" => UndoResult.NotFound,
        "failed" => UndoResult.Failed,
        _ => throw new FormatException($"不明な戻しの結果です: {value}"),
    };
}
