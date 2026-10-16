namespace AutoMailChecker.Core.Configuration;

/// <summary>
/// 既定の設定。初回起動時に書き出す雛形（コメント付き）と、誤りのある部分を置き換える既定値を兼ねる。
/// </summary>
public static class DefaultConfig
{
    private const string ResourceName = "AutoMailChecker.Core.DefaultConfig.jsonc";

    private static readonly Lazy<string> TemplateTextLazy = new(LoadTemplate);
    private static readonly Lazy<AppConfig> InstanceLazy = new(() => ConfigJson.Deserialize(TemplateText));

    /// <summary>初回起動時に config.jsonc として書き出す内容。</summary>
    public static string TemplateText => TemplateTextLazy.Value;

    public static AppConfig Instance => InstanceLazy.Value;

    private static string LoadTemplate()
    {
        using var stream = typeof(DefaultConfig).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"埋め込みリソース {ResourceName} が見つかりません。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
