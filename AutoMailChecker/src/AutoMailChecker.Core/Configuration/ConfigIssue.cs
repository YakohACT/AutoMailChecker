namespace AutoMailChecker.Core.Configuration;

/// <summary>設定ファイルの区分。ファイルを直接編集して誤りがあったときは、この単位で前の値に戻す。</summary>
public enum ConfigSection
{
    /// <summary>ファイル全体（JSON として読めない）</summary>
    File,
    Accounts,
    IgnoreSenders,
    Items,
    Advertisement,
    Policy,
    Trial,
    History,
    Llm,
    Pause,
}

/// <summary>設定の誤り1件。Path は「items[2].priority」のような位置。</summary>
public sealed record ConfigIssue(ConfigSection Section, string Path, string Message)
{
    public override string ToString() => string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}
