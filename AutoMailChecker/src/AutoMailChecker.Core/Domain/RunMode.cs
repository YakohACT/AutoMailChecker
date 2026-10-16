namespace AutoMailChecker.Core;

/// <summary>運転モード。複数の条件が重なったときは Stopped → Held → LowLoad の順に強いほうを採る。</summary>
public enum RunMode
{
    /// <summary>稼働中: IMAP 監視・AI 判定ともに行い、llama-server は GPU 使用で起動</summary>
    Running,
    /// <summary>低負荷で継続: llama-server を CPU のみ・2 スレッドで起動し直して判定を続ける</summary>
    LowLoad,
    /// <summary>判定保留: IMAP 監視はキューに積むだけ。llama-server は起動したまま</summary>
    Held,
    /// <summary>完全停止: IMAP 監視はキューに積むだけ。llama-server は終了してメモリを空ける</summary>
    Stopped,
}

/// <summary>llama-server の起動設定。</summary>
public enum LlmProfile
{
    Normal,
    LowLoad,
}

/// <summary>運転モードと、その理由（トレイのツールチップに出す）。</summary>
/// <param name="Reason">例:「Steamのゲームを検知（xxx.exe）」「VS Code起動中」</param>
/// <param name="HeldUntil">一時停止の終わる日時。「再開するまで」なら null</param>
public sealed record RunModeState(RunMode Mode, string? Reason, DateTimeOffset? HeldUntil)
{
    public static RunModeState Running { get; } = new(RunMode.Running, null, null);
}

/// <summary>検知した停止対象1つ。Key はプロセスやルールごとに決まり、手動で再開したときに「この原因は無視する」のに使う。</summary>
public sealed record PauseDetection(string Key, Configuration.PauseAction Action, string Description);
