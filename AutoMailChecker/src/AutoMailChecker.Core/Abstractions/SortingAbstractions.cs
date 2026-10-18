using AutoMailChecker.Core.State;

namespace AutoMailChecker.Core;

/// <summary>判定履歴画面の「受信トレイに戻す」。ユーザーが押したときだけ動き、AI や自動処理からは呼ばない。</summary>
public interface IUndoService
{
    /// <param name="asMisjudgement">
    /// true なら誤判定として記録する（判定履歴から1件戻したとき）。
    /// お試し仕分けの「まとめて戻す」は誤判定ではないので false にする（精度の集計を崩さないため）。
    /// </param>
    Task<UndoOutcome> UndoAsync(long judgementId, bool asMisjudgement, CancellationToken ct);
}

/// <summary>戻した結果と、詳細欄に出す文言。</summary>
public sealed record UndoOutcome(UndoResult Result, string Message);

/// <summary>お試し仕分け（仕様書9章、シーケンス SQ-4）。</summary>
public interface ITrialService
{
    /// <summary>開始前に、対象件数と所要時間の見込みを出す。</summary>
    Task<TrialEstimate> EstimateAsync(TrialRequest request, CancellationToken ct);

    /// <summary>対象を集めて処理待ちに積む。新着の監視とは独立して動き、新着が常に先に処理される。</summary>
    Task<TrialRun> StartAsync(TrialRequest request, CancellationToken ct);

    Task CancelAsync(long trialRunId, CancellationToken ct);

    /// <summary>その回に実際に移動したメールを、まとめて受信トレイへ戻す。</summary>
    Task<RollbackSummary> RollbackAsync(long trialRunId, CancellationToken ct);

    /// <summary>項目ごとの件数、確信度「低」・該当なし・失敗の件数と、誤判定の記録から出した精度。</summary>
    Task<TrialReport> GetReportAsync(long trialRunId, CancellationToken ct);
}

/// <param name="Folder">既定は受信トレイ。ほかのフォルダーでは「結果の表示のみ」に固定する</param>
public sealed record TrialRequest(
    DateOnly From, DateOnly To, IReadOnlyList<string> Accounts, string Folder, TrialMode Mode, int MaxMessages);

/// <param name="Found">期間内に見つかった件数</param>
/// <param name="Selected">上限で間引いたあとの件数</param>
public sealed record TrialEstimate(int Found, int Selected, TimeSpan PerMail, TimeSpan Total);

public sealed record RollbackSummary(int Restored, int NotFound, int Failed);
