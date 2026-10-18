using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Core;

/// <summary>
/// 設定画面の振り分け先の候補（画面設計 SC-02）。画面を開いたときに各アカウントへ問い合わせ、10 分間使い回す。
/// </summary>
public interface IMailFolderCatalog
{
    /// <summary>振り分け先の候補（受信トレイ・迷惑メール・特別なフォルダーを除く）。refresh なら問い合わせ直す。</summary>
    Task<IReadOnlyList<string>> GetSortingFoldersAsync(string accountId, bool refresh, CancellationToken ct);
}

/// <summary>全般タブの「接続テスト」。保存前のアカウント設定と、登録済みのパスワードで接続してみる。</summary>
public interface IMailConnectionTester
{
    /// <summary>結果を1行で返す（例:「接続できました（フォルダー 12 個）」）。失敗したら MailServerException。</summary>
    Task<string> TestAsync(AccountConfig account, CancellationToken ct);
}

/// <summary>全般タブの AI の「接続テスト」。保存前の接続先 URL と、登録済みの API キーで繋いでみる。</summary>
public interface ILlmConnectionTester
{
    /// <summary>結果を1行で返す（例:「接続できました（モデル: Qwen3.5-9B-Q4_K_M）」）。失敗したら LlmStartException。</summary>
    Task<string> TestAsync(string endpoint, CancellationToken ct);
}

/// <summary>
/// 項目タブの「試し判定」。判定履歴の1通を、保存前の設定で判定し直して結果だけを返す。移動はしない。
/// 本文はサーバーから BODY.PEEK で取り直して使い、保存しない。
/// </summary>
public interface IRejudgeService
{
    /// <summary>AI が判定に使えるか（完全停止中は試し判定できない）。</summary>
    bool IsAvailable { get; }

    Task<RejudgeResult> RejudgeAsync(long judgementId, AppConfig draft, CancellationToken ct);
}

/// <param name="Destination">保存前の設定で振り分けるとしたら、どこへ移るか（例:「AI/重要」「受信トレイに残す（確信度が低い）」）</param>
public sealed record RejudgeResult(Judgement Judgement, IReadOnlyList<string> MatchedLabels, string Destination, TimeSpan Duration);
