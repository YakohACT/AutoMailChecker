using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Tests.Core;

public class ConfigLoaderTests
{
    private static readonly AppConfig Fallback = DefaultConfig.Instance with
    {
        History = new HistoryConfig { RetentionDays = 60 },
    };

    [Fact]
    public void JSONとして読めなければ_前の設定のまま動く()
    {
        var result = ConfigLoader.Load("{ \"accounts\": [ ", Fallback);

        Assert.Same(Fallback, result.Config);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(ConfigSection.File, issue.Section);
        Assert.Contains("行目付近", issue.Message);
    }

    [Fact]
    public void 誤りのある区分だけ前の値で動かす()
    {
        var edited = DefaultConfig.Instance with
        {
            History = new HistoryConfig { RetentionDays = 3 },   // 範囲外
            Policy = DefaultConfig.Instance.Policy with { AutoSort = true },
        };

        var result = ConfigLoader.Load(ConfigJson.Serialize(edited), Fallback);

        Assert.Equal(60, result.Config.History.RetentionDays);
        Assert.True(result.Config.Policy.AutoSort);
        Assert.Contains(result.Issues, i => i.Section == ConfigSection.History);
    }

    [Fact]
    public void 誤りがなければそのまま使う()
    {
        var edited = DefaultConfig.Instance with { Trial = new TrialConfig { MaxMessages = 50 } };

        var result = ConfigLoader.Load(ConfigJson.Serialize(edited), Fallback);

        Assert.False(result.HasIssues);
        Assert.Equal(50, result.Config.Trial.MaxMessages);
    }

    [Fact]
    public void 書き出した設定を読み直すと同じ内容になる()
    {
        var json = ConfigJson.Serialize(DefaultConfig.Instance);
        var reread = ConfigJson.Deserialize(json);

        Assert.Equal(json, ConfigJson.Serialize(reread));
        Assert.Contains("\"minConfidence\": \"medium\"", json);
        Assert.Contains("\"mode\": \"lowLoad\"", json);
        Assert.Contains("\"auth\": \"appPassword\"", json);
        Assert.Contains("予約関連", json); // 日本語をエスケープしない
    }
}
