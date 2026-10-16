using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace AutoMailChecker.Core.Configuration;

/// <summary>config.jsonc（コメント付き JSON）の読み書き。</summary>
public static class ConfigJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>JSON として読めなければ <see cref="JsonException"/> を投げる。</summary>
    public static AppConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, Options)
        ?? throw new JsonException("設定ファイルの中身が空です。");

    /// <summary>画面から保存するときに使う。コメントは残らない。</summary>
    public static string Serialize(AppConfig config) =>
        JsonSerializer.Serialize(config, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            // 日本語をそのまま書き出し、手で編集しやすくする
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
