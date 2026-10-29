namespace AutoMailChecker.Core;

/// <summary>
/// 1通を AI で判定する。
/// AI の出力が不正、または時間切れで判定できなければ <see cref="ClassificationException"/>（そのメールは「判定失敗」）。
/// AI サーバーに繋がらなければ <see cref="LlmUnavailableException"/>（そのメールは処理待ちに戻す）。
/// </summary>
public interface IMailClassifier
{
    /// <summary>指示文の雛形の版（判定履歴の prompt_version）。</summary>
    string PromptVersion { get; }

    Task<Judgement> ClassifyAsync(MailSnapshot mail, IReadOnlyList<ItemDefinition> items, CancellationToken ct);
}

/// <summary>
/// AI サーバーの準備と後始末。アプリが llama-server を起動・終了する場合と、
/// 接続先 URL の起動済みサーバーに繋ぐだけの場合（<see cref="IsExternal"/>）がある。
/// </summary>
public interface ILlmRuntime
{
    /// <summary>判定に使える状態の設定。止まっている、または準備中なら null。</summary>
    LlmProfile? Running { get; }

    /// <summary>読み込んでいるモデルの名前（判定履歴の model_name）。</summary>
    string ModelName { get; }

    /// <summary>
    /// 接続先 URL の起動済みサーバーを使う（アプリは起動・終了しない）なら true。
    /// このときは完全停止してもサーバーのメモリは空かず、低負荷の設定にも切り替えられない。
    /// </summary>
    bool IsExternal { get; }

    /// <summary>判定に使えるようになるまで待つ。準備できなければ <see cref="LlmStartException"/>。</summary>
    Task StartAsync(LlmProfile profile, CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// 使っているサーバーが予期せず終了する（または繋がらなくなる）まで待つ（意図して止めたときは完了しない）。
    /// 一度終了したら、次に <see cref="StartAsync"/> が成功するまで完了したままなので、起動できたときだけ待つこと。
    /// </summary>
    Task WaitForUnexpectedExitAsync(CancellationToken ct);
}

/// <summary>AI の出力が不正、または時間切れで判定できなかった。メールは移動せず「判定失敗」として記録する。</summary>
public sealed class ClassificationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>AI サーバーが止まっている、または繋がらない。AI のせいではないので、メールは処理待ちに戻して後で判定する。</summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>AI サーバーを準備できなかった（実行ファイルやモデルがない、接続先に繋がらない、/health が時間内に応答しない、など）。</summary>
public sealed class LlmStartException(string message, Exception? inner = null) : Exception(message, inner);
