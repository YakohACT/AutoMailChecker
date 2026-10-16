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
