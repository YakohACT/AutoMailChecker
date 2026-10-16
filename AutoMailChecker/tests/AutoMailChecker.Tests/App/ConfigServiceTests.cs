using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Hosting;
using AutoMailChecker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.App;

public sealed class ConfigServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;

    public ConfigServiceTests()
    {
        _paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void 初回起動時はコメント付きの雛形を書き出す()
    {
        using var service = CreateService();

        var text = File.ReadAllText(_paths.ConfigFile);
        Assert.Equal(DefaultConfig.TemplateText, text);
        Assert.Contains("// ", text);
        Assert.Empty(service.Issues);
        Assert.Equal(4, service.Current.Items.Count);
    }

    [Fact]
    public async Task 画面から保存すると_前の内容をbakへ退避して変更を通知する()
    {
        using var service = CreateService();
        ConfigChangedEventArgs? raised = null;
        service.Changed += (_, e) => raised = e;
        var edited = service.Current with { Policy = service.Current.Policy with { AutoSort = true } };

        var result = await service.SaveAsync(edited, Ct);

        Assert.True(result.Saved);
        Assert.False(result.RestartRequired);
        Assert.Equal(DefaultConfig.TemplateText, File.ReadAllText(_paths.ConfigBackupFile));
        Assert.True(ConfigJson.Deserialize(File.ReadAllText(_paths.ConfigFile)).Policy.AutoSort);
        Assert.True(service.Current.Policy.AutoSort);
        Assert.NotNull(raised);
        Assert.True(raised.Current.Policy.AutoSort);
        Assert.False(raised.Previous.Policy.AutoSort);
    }

    [Fact]
    public async Task 誤りがあれば保存しない()
    {
        using var service = CreateService();
        var before = File.ReadAllText(_paths.ConfigFile);
        var items = service.Current.Items.ToList();
        items[1] = items[1] with { Priority = items[0].Priority };

        var result = await service.SaveAsync(service.Current with { Items = items }, Ct);

        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Path == "items[1].priority");
        Assert.Equal(before, File.ReadAllText(_paths.ConfigFile));
        Assert.False(File.Exists(_paths.ConfigBackupFile));
    }

    [Fact]
    public async Task アカウントを変えたら再起動が必要と知らせる()
    {
        using var service = CreateService();
        var accounts = service.Current.Accounts.ToList();
        accounts[0] = accounts[0] with { User = "someone" };

        var result = await service.SaveAsync(service.Current with { Accounts = accounts }, Ct);

        Assert.True(result.Saved);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task ファイルを直接編集したら再起動なしで反映する()
    {
        using var service = CreateService();
        ConfigChangedEventArgs? raised = null;
        service.Changed += (_, e) => raised = e;

        await File.WriteAllTextAsync(_paths.ConfigFile,
            ConfigJson.Serialize(service.Current with { History = new HistoryConfig { RetentionDays = 30 } }), Ct);
        await service.ReloadFromFileAsync();

        Assert.Equal(30, service.Current.History.RetentionDays);
        Assert.NotNull(raised);
        Assert.Empty(service.Issues);
    }

    [Fact]
    public async Task 直接編集に誤りがあれば_その区分だけ前の値で動かして知らせる()
    {
        using var service = CreateService();

        await File.WriteAllTextAsync(_paths.ConfigFile,
            ConfigJson.Serialize(service.Current with
            {
                History = new HistoryConfig { RetentionDays = 3 },
                Trial = new TrialConfig { MaxMessages = 20 },
            }), Ct);
        await service.ReloadFromFileAsync();

        Assert.Equal(90, service.Current.History.RetentionDays);
        Assert.Equal(20, service.Current.Trial.MaxMessages);
        Assert.Contains(service.Issues, i => i.Section == ConfigSection.History);
    }

    [Fact]
    public async Task 壊れたファイルなら前の設定のまま動く()
    {
        using var service = CreateService();
        var before = service.Current;

        await File.WriteAllTextAsync(_paths.ConfigFile, "{ broken", Ct);
        await service.ReloadFromFileAsync();

        Assert.Same(before, service.Current);
        Assert.Contains(service.Issues, i => i.Section == ConfigSection.File);
    }

    [Fact]
    public async Task 自分で保存したファイルの変更通知では読み直さない()
    {
        using var service = CreateService();
        await service.SaveAsync(service.Current with { Trial = new TrialConfig { MaxMessages = 10 } }, Ct);
        var raisedCount = 0;
        service.Changed += (_, _) => raisedCount++;

        await service.ReloadFromFileAsync();

        Assert.Equal(0, raisedCount);
    }

    private ConfigService CreateService() => new(_paths, NullLogger<ConfigService>.Instance);
}
