using System.Text.Json;

namespace AutoMailChecker.Core.Configuration;

/// <summary>読み込んだ設定と、見つかった誤り。</summary>
public sealed record ConfigLoadResult(AppConfig Config, IReadOnlyList<ConfigIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>
/// ファイルを直接編集した場合の読み込み。誤りのある区分だけ前の値（なければ既定値）で動かす（外部仕様タブ）。
/// </summary>
public static class ConfigLoader
{
    /// <param name="json">config.jsonc の中身</param>
    /// <param name="fallback">誤りのある区分の代わりに使う設定。直前に動いていた設定か、なければ既定値。</param>
    public static ConfigLoadResult Load(string json, AppConfig fallback)
    {
        AppConfig parsed;
        try
        {
            parsed = ConfigJson.Deserialize(json);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? Translate.Text(
                $"{line + 1}行目付近",
                $"near line {line + 1}") : Translate.Text("位置不明", "position unknown");
            return new ConfigLoadResult(fallback, [new ConfigIssue(ConfigSection.File, "", Translate.Text(
                $"設定ファイルを読めません（{where}）: {ex.Message}",
                $"Cannot read the settings file ({where}): {ex.Message}"))]);
        }

        var issues = ConfigValidator.Validate(parsed);
        if (issues.Count == 0)
        {
            return new ConfigLoadResult(parsed, issues);
        }

        var merged = ReplaceSections(parsed, fallback, issues.Select(i => i.Section).ToHashSet());

        // 区分をまたぐ誤り（項目の振り分け先に存在しないアカウントを書いた、など）が残るときは、前の設定をそのまま使う
        if (ConfigValidator.Validate(merged).Count > 0)
        {
            merged = fallback;
        }

        return new ConfigLoadResult(merged, issues);
    }

    internal static AppConfig ReplaceSections(AppConfig config, AppConfig fallback, IReadOnlySet<ConfigSection> sections)
    {
        if (sections.Contains(ConfigSection.File))
        {
            return fallback;
        }

        return config with
        {
            Accounts = sections.Contains(ConfigSection.Accounts) ? fallback.Accounts : config.Accounts,
            IgnoreSenders = sections.Contains(ConfigSection.IgnoreSenders) ? fallback.IgnoreSenders : config.IgnoreSenders,
            Items = sections.Contains(ConfigSection.Items) ? fallback.Items : config.Items,
            Policy = sections.Contains(ConfigSection.Policy) ? fallback.Policy : config.Policy,
            Trial = sections.Contains(ConfigSection.Trial) ? fallback.Trial : config.Trial,
            History = sections.Contains(ConfigSection.History) ? fallback.History : config.History,
            Llm = sections.Contains(ConfigSection.Llm) ? fallback.Llm : config.Llm,
            Pause = sections.Contains(ConfigSection.Pause) ? fallback.Pause : config.Pause,
            Ui = sections.Contains(ConfigSection.Ui) ? fallback.Ui : config.Ui,
        };
    }
}
