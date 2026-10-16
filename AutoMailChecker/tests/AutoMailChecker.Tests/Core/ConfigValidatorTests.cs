using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Tests.Core;

public class ConfigValidatorTests
{
    private static readonly AppConfig Default = DefaultConfig.Instance;

    [Fact]
    public void 既定の設定はコメント付きで読めて_誤りがない()
    {
        Assert.Empty(ConfigValidator.Validate(Default));

        Assert.Equal(["icloud", "gmail1", "gmail2", "gmail3"], Default.Accounts.Select(a => a.Id));
        Assert.Equal(["important", "reservation", "payment", "payslip"], Default.Items.Select(i => i.Id.Value));
        Assert.Equal(AdvertisementMode.Ignore, Default.Advertisement.Mode);
        Assert.Equal(Confidence.Medium, Default.Policy.MinConfidence);
        Assert.False(Default.Policy.AutoSort);
        Assert.Null(Default.Llm.NCpuMoe);
        Assert.Equal(10, Default.Pause.Apps.Count);
        Assert.Equal(PauseAction.LowLoad, Default.Pause.Apps.Single(a => a.Name == "VS Code").Mode);
        Assert.Equal("雀魂", Default.Pause.Apps.Single(a => a.Name == "雀魂").TitleContains);
        Assert.Equal("AutoMailChecker/gmail2", Default.Accounts[2].EffectiveCredentialKey);
    }

    [Fact]
    public void 資格情報の名前を省略したら_アプリ名とアカウントIDから作る()
    {
        var account = new AccountConfig { Id = "gmail9" };

        Assert.Equal("AutoMailChecker/gmail9", account.EffectiveCredentialKey);
    }

    [Fact]
    public void 優先度の重複は誤り()
    {
        var issues = Validate(WithItem(1, i => i with { Priority = 1 }));

        Assert.Contains(issues, i => i.Path == "items[1].priority" && i.Section == ConfigSection.Items);
    }

    [Fact]
    public void 項目IDの重複は誤り()
    {
        var issues = Validate(WithItem(1, i => i with { Id = new ItemId("important") }));

        Assert.Contains(issues, i => i.Path == "items[1].id");
    }

    [Fact]
    public void 広告用の予約IDは項目に使えない()
    {
        var issues = Validate(WithItem(0, i => i with { Id = new ItemId("ad") }));

        Assert.Contains(issues, i => i.Path == "items[0].id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    public void 表示名は1から20文字(string label)
    {
        var issues = Validate(WithItem(0, i => i with { Label = label }));

        Assert.Contains(issues, i => i.Path == "items[0].label");
    }

    [Fact]
    public void 例は3件まで()
    {
        var issues = Validate(WithItem(0, i => i with { Examples = ["a", "b", "c", "d"] }));

        Assert.Contains(issues, i => i.Path == "items[0].examples");
    }

    [Fact]
    public void 振り分け先に存在しないアカウントを書くと誤り()
    {
        var issues = Validate(WithItem(0, i => i with
        {
            Folders = new Dictionary<string, string>(i.Folders) { ["other"] = "重要" },
        }));

        Assert.Contains(issues, i => i.Path == "items[0].folders.other");
    }

    [Fact]
    public void 受信トレイは振り分け先にできない()
    {
        var issues = Validate(WithItem(0, i => i with
        {
            Folders = new Dictionary<string, string>(i.Folders) { ["gmail1"] = "Inbox" },
        }));

        Assert.Contains(issues, i => i.Path == "items[0].folders.gmail1");
    }

    [Fact]
    public void 同じアカウントで振り分け先が広告用と重なると誤り()
    {
        var config = Default with
        {
            Advertisement = Default.Advertisement with
            {
                Folders = new Dictionary<string, string>(Default.Advertisement.Folders) { ["gmail1"] = "AI/重要" },
            },
        };

        var issues = Validate(config);

        Assert.Contains(issues, i => i.Path == "advertisement.folders.gmail1" && i.Section == ConfigSection.Advertisement);
    }

    [Fact]
    public void 別のアカウントなら同じフォルダー名でもよい()
    {
        // icloud の「重要」と gmail1 の「重要」は別物
        var issues = Validate(WithItem(0, i => i with
        {
            Folders = new Dictionary<string, string>(i.Folders) { ["gmail1"] = "重要" },
        }));

        Assert.Empty(issues);
    }

    [Fact]
    public void しきい値に低は指定できない()
    {
        var issues = Validate(Default with { Policy = Default.Policy with { MinConfidence = Confidence.Low } });

        Assert.Contains(issues, i => i.Path == "policy.minConfidence");
    }

    [Fact]
    public void 数値が範囲外なら誤り()
    {
        var config = Default with
        {
            Trial = new TrialConfig { MaxMessages = 0 },
            History = new HistoryConfig { RetentionDays = 400 },
            Pause = Default.Pause with { PollSeconds = 1, ResumeDelaySeconds = 601 },
        };

        var paths = Validate(config).Select(i => i.Path).ToList();

        Assert.Contains("trial.maxMessages", paths);
        Assert.Contains("history.retentionDays", paths);
        Assert.Contains("pause.pollSeconds", paths);
        Assert.Contains("pause.resumeDelaySeconds", paths);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("@")]
    [InlineData("a@b@c")]
    [InlineData("a b@example.com")]
    public void 除外リストの書式が違えば誤り(string sender)
    {
        var issues = Validate(Default with { IgnoreSenders = [sender] });

        Assert.Contains(issues, i => i.Path == "ignoreSenders[0]");
    }

    [Theory]
    [InlineData("gmail_1")]
    [InlineData("")]
    [InlineData("メール")]
    public void アカウントIDは英数字とハイフンのみ(string id)
    {
        var accounts = Default.Accounts.ToList();
        accounts[0] = accounts[0] with { Id = id };

        var issues = Validate(Default with { Accounts = accounts, Items = [], Advertisement = new AdvertisementConfig() });

        Assert.Contains(issues, i => i.Path == "accounts[0].id");
    }

    [Fact]
    public void 停止対象は実行ファイル名かタイトルのどちらかが要る()
    {
        var issues = Validate(Default with { Pause = Default.Pause with { Apps = [new PauseAppRule { Name = "空" }] } });

        Assert.Contains(issues, i => i.Path == "pause.apps[0]");
    }

    private static IReadOnlyList<ConfigIssue> Validate(AppConfig config) => ConfigValidator.Validate(config);

    private static AppConfig WithItem(int index, Func<ItemDefinition, ItemDefinition> change)
    {
        var items = Default.Items.ToList();
        items[index] = change(items[index]);
        return Default with { Items = items };
    }
}
