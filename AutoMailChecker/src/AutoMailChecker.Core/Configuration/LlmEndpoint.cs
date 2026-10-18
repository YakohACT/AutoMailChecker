namespace AutoMailChecker.Core.Configuration;

/// <summary>
/// 起動済みの AI サーバー（llama-server など OpenAI 互換のもの）の接続先 URL（llm.endpoint）。
/// メール本文を送る先なので、この PC（127.0.0.1・::1・localhost）だけに限る。
/// </summary>
public static class LlmEndpoint
{
    /// <summary>llama-server を引数なしで起動したときの待ち受け先。</summary>
    public const string DefaultUrl = "http://127.0.0.1:8080";

    /// <summary>API キーを置く資格情報マネージャーの名前（アカウント ID に使えない「.」を入れ、アカウントと重ならないようにする）。</summary>
    public const string ApiKeyCredentialKey = AppInfo.Name + "/llm.apiKey";

    private const string FormatMessage =
        "接続先 URL は http://127.0.0.1:8080 のように、この PC のアドレス（127.0.0.1 または localhost）で指定してください。";

    /// <summary>
    /// URL を確かめ、要求の基点にする（末尾に / を付け、OpenAI 互換の「/v1」で終わっていれば外す）。
    /// 使えなければ null を返し、理由を error に入れる。
    /// </summary>
    public static Uri? TryParse(string? text, out string? error)
    {
        error = FormatMessage;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return null;
        }

        if (!uri.IsLoopback)
        {
            error = $"「{uri.Host}」には接続できません。メール本文がこの PC の外に出ないよう、この PC のアドレス（127.0.0.1 または localhost）だけを指定できます。";
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        error = null;
        return new UriBuilder(uri) { Path = path + "/" }.Uri;
    }
}
