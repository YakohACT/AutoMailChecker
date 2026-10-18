namespace AutoMailChecker.Core.Configuration;

/// <summary>
/// 設定の読み書き。ファイルの変更は監視で検知し、再起動なしで反映する。
/// アカウントと llm の endpoint・serverExe・model・port・ctxSize は、使う側が起動時の値を持ち続ける（再起動で反映）。
/// </summary>
public interface IConfigProvider
{
    /// <summary>いま有効な設定。</summary>
    AppConfig Current { get; }

    /// <summary>いま有効な設定を読み込んだときに見つかった誤り（トレイアイコンで知らせる）。</summary>
    IReadOnlyList<ConfigIssue> Issues { get; }

    event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <summary>画面からの保存。誤りが1つでもあれば保存しない。保存前の内容は .bak に退避する。</summary>
    Task<ConfigSaveResult> SaveAsync(AppConfig config, CancellationToken ct);
}

public sealed class ConfigChangedEventArgs(
    AppConfig previous, AppConfig current, IReadOnlyList<ConfigIssue> issues, bool restartRequired) : EventArgs
{
    public AppConfig Previous { get; } = previous;
    public AppConfig Current { get; } = current;
    public IReadOnlyList<ConfigIssue> Issues { get; } = issues;

    /// <summary>アカウントやモデルなど、再起動しないと反映されない値が変わった。</summary>
    public bool RestartRequired { get; } = restartRequired;
}

public sealed record ConfigSaveResult(bool Saved, IReadOnlyList<ConfigIssue> Issues, bool RestartRequired)
{
    public static ConfigSaveResult Rejected(IReadOnlyList<ConfigIssue> issues) => new(false, issues, false);
}
