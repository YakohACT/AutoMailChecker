namespace AutoMailChecker.Core;

/// <summary>1通を AI で判定する。判定に失敗したら <see cref="ClassificationException"/> を投げる。</summary>
public interface IMailClassifier
{
    Task<Judgement> ClassifyAsync(MailSnapshot mail, IReadOnlyList<ItemDefinition> items, CancellationToken ct);
}

/// <summary>llama-server の起動と終了。</summary>
public interface ILlmRuntime
{
    /// <summary>起動中の設定。止まっていれば null。</summary>
    LlmProfile? Running { get; }

    /// <summary>/health が 200 になるまで待つ。</summary>
    Task StartAsync(LlmProfile profile, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

/// <summary>AI の出力が不正、または時間切れで判定できなかった。メールは移動せず「判定失敗」として記録する。</summary>
public sealed class ClassificationException(string message, Exception? inner = null) : Exception(message, inner);
