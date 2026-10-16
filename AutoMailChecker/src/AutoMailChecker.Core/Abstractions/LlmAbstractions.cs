namespace AutoMailChecker.Core;

/// <summary>
/// 1通を AI で判定する。
/// AI の出力が不正、または時間切れで判定できなければ <see cref="ClassificationException"/>（そのメールは「判定失敗」）。
/// llama-server に繋がらなければ <see cref="LlmUnavailableException"/>（そのメールは処理待ちに戻す）。
/// </summary>
public interface IMailClassifier
{
    /// <summary>指示文の雛形の版（判定履歴の prompt_version）。</summary>
    string PromptVersion { get; }

    Task<Judgement> ClassifyAsync(MailSnapshot mail, IReadOnlyList<ItemDefinition> items, CancellationToken ct);
}

/// <summary>llama-server の起動と終了。</summary>
public interface ILlmRuntime
{
    /// <summary>起動中の設定。止まっている、または準備中なら null。</summary>
    LlmProfile? Running { get; }

    /// <summary>読み込んでいるモデルの名前（判定履歴の model_name）。</summary>
    string ModelName { get; }

    /// <summary>/health が 200 になるまで待つ。起動できなければ <see cref="LlmStartException"/>。</summary>
    Task StartAsync(LlmProfile profile, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

/// <summary>AI の出力が不正、または時間切れで判定できなかった。メールは移動せず「判定失敗」として記録する。</summary>
public sealed class ClassificationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>llama-server が止まっている、または繋がらない。AI のせいではないので、メールは処理待ちに戻して後で判定する。</summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>llama-server を起動できなかった（実行ファイルやモデルがない、/health が時間内に応答しない、など）。</summary>
public sealed class LlmStartException(string message, Exception? inner = null) : Exception(message, inner);
