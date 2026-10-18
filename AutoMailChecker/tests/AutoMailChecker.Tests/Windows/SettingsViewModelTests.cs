using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Persistence;
using AutoMailChecker.Windows.Pause;
using AutoMailChecker.Windows.Settings;
using AutoMailChecker.Windows.Startup;

namespace AutoMailChecker.Tests.Windows;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ValidatingConfig _config = new(DefaultConfig.Instance);
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeCatalog _catalog = new();
    private readonly MemoryCredentials _credentials = new();
    private readonly FakeAutoStart _autoStart = new();
    private readonly FakeLlmTester _llmTester = new();
    private readonly SqliteStateStore _store;

    public SettingsViewModelTests()
    {
        _store = new SqliteStateStore(_temp.Combine("state.db"), TimeProvider.System);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void 何も変えずに組み立てると今の設定と同じになる()
    {
        var viewModel = Create();

        Assert.Equal(ConfigJson.Serialize(DefaultConfig.Instance), ConfigJson.Serialize(viewModel.BuildConfig()));
    }

    [Fact]
    public void 一覧の並び順がそのまま優先度になる()
    {
        var viewModel = Create();
        var payment = viewModel.ItemsTab.Items.Single(i => i.Id.Value == "payment");

        viewModel.ItemsTab.Move(payment, 0);

        var items = viewModel.BuildConfig().Items;
        Assert.Equal(["payment", "important", "reservation", "payslip"], items.Select(i => i.Id.Value));
        Assert.Equal([1, 2, 3, 4], items.Select(i => i.Priority));
        Assert.Equal("1. 決済履歴", payment.ListText);
    }

    [Fact]
    public void 項目を追加するとIDを自動で振り_削除は確認してから行う()
    {
        var viewModel = Create();

        viewModel.ItemsTab.AddCommand.Execute(null);
        var added = viewModel.ItemsTab.SelectedItem!;
        _dialogs.ConfirmResult = false;
        viewModel.ItemsTab.DeleteCommand.Execute(null);

        Assert.Matches("^item_[0-9a-f]{4}$", added.Id.Value);
        Assert.Equal(5, added.Priority);
        Assert.Equal(5, viewModel.ItemsTab.Items.Count); // 確認で取り消した

        _dialogs.ConfirmResult = true;
        viewModel.ItemsTab.DeleteCommand.Execute(null);
        Assert.Equal(4, viewModel.ItemsTab.Items.Count);
    }

    [Fact]
    public void 例は1行に1例として読む()
    {
        var viewModel = Create();
        var item = viewModel.ItemsTab.Items[0];

        item.ExamplesText = "書類の提出依頼\r\n\r\n  支払期限のある請求  \n";

        Assert.Equal(["書類の提出依頼", "支払期限のある請求"], viewModel.BuildConfig().Items[0].Examples);
    }

    [Fact]
    public async Task 振り分け先の候補は_そのアカウントのフォルダーから受信トレイ_迷惑メール_使用中のものを除く()
    {
        var viewModel = Create();
        _catalog.Folders["gmail1"] = ["AI/重要", "AI/予約関連", "AI/決済履歴", "AI/給与明細", "AI/広告", "AI/その他"];

        await viewModel.ItemsTab.LoadFoldersAsync(refresh: false);

        var important = viewModel.ItemsTab.Items[0].Folders.Single(f => f.AccountId == "gmail1");
        Assert.Equal(["AI/重要", "AI/その他"], important.Suggestions); // 自分の「AI/重要」は残す
        Assert.Contains("icloud", viewModel.ItemsTab.FolderStatus); // 取れなかったアカウントは知らせる
    }

    [Fact]
    public async Task 誤りがあれば保存せず_該当欄に誤りを付ける()
    {
        var viewModel = Create();
        viewModel.ItemsTab.Items[1].Label = "";
        viewModel.ItemsTab.Items[0].Folders.Single(f => f.AccountId == "gmail1").Folder = "AI/予約関連"; // 予約関連と重なる
        viewModel.GeneralTab.RetentionDaysText = "400";

        await SaveAsync(viewModel);

        Assert.Null(_config.Saved);
        Assert.True(viewModel.StatusIsError);
        Assert.True(viewModel.ItemsTab.Items[1].GetErrors(nameof(ItemEditViewModel.Label)).Cast<string>().Any());
        Assert.True(viewModel.GeneralTab.GetErrors(nameof(GeneralTabViewModel.RetentionDaysText)).Cast<string>().Any());
        Assert.Contains(viewModel.Issues, i => i.StartsWith("2 番目の項目: ", StringComparison.Ordinal)); // 表示名が空なら何番目かで示す
        Assert.Contains(viewModel.Issues, i => i.Contains("予約関連", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 数字の欄が数字でなければ保存しない()
    {
        var viewModel = Create();
        viewModel.PauseTab.ResumeDelayText = "一分";

        await SaveAsync(viewModel);

        Assert.Null(_config.Saved);
        Assert.True(viewModel.PauseTab.GetErrors(nameof(PauseTabViewModel.ResumeDelayText)).Cast<string>().Any());
    }

    [Fact]
    public async Task 保存すると設定ファイルに書き戻し_停止対象や全般の変更も含める()
    {
        var viewModel = Create();
        viewModel.ItemsTab.AdvertisementMove = true;
        viewModel.PauseTab.Rules.Single(r => r.Name == "Unity").Action = PauseAction.Stop;
        viewModel.PauseTab.FullscreenDetect = false;
        viewModel.GeneralTab.HighConfidenceOnly = true;
        viewModel.GeneralTab.ErrorPopup = true;

        await SaveAsync(viewModel);

        var saved = Assert.IsType<AppConfig>(_config.Saved);
        Assert.Equal(AdvertisementMode.Move, saved.Advertisement.Mode);
        Assert.Equal(PauseAction.Stop, saved.Pause.Apps.Single(a => a.Name == "Unity").Mode);
        Assert.False(saved.Pause.FullscreenDetect);
        Assert.Equal(Confidence.High, saved.Policy.MinConfidence);
        Assert.True(saved.Policy.ErrorPopup);
        Assert.False(viewModel.StatusIsError);
    }

    [Fact]
    public void お試し仕分けの前に自動仕分けをオンにするときは確認する()
    {
        var viewModel = Create();

        _dialogs.ConfirmResult = false;
        viewModel.GeneralTab.AutoSort = true;
        Assert.False(viewModel.GeneralTab.AutoSort);

        _dialogs.ConfirmResult = true;
        viewModel.GeneralTab.AutoSort = true;
        Assert.True(viewModel.GeneralTab.AutoSort);
    }

    [Fact]
    public void アカウントを追加すると_項目の振り分け先の欄も増える()
    {
        var viewModel = Create();

        viewModel.GeneralTab.AddGmailCommand.Execute(null);

        var added = viewModel.GeneralTab.SelectedAccount!;
        Assert.Equal("gmail4", added.Id);
        Assert.True(added.IsNew);
        Assert.Contains(viewModel.ItemsTab.Items[0].Folders, f => f.AccountId == "gmail4");
        Assert.Contains(viewModel.ItemsTab.AdFolders, f => f.AccountId == "gmail4");
        Assert.Equal("imap.gmail.com", viewModel.BuildConfig().Accounts[^1].Host);
    }

    [Fact]
    public void パスワードは資格情報マネージャーにだけ登録する()
    {
        var viewModel = Create();
        viewModel.GeneralTab.SelectedAccount = viewModel.GeneralTab.Accounts[1];
        _dialogs.Password = "abcd efgh ijkl mnop";

        viewModel.GeneralTab.RegisterPasswordCommand.Execute(null);

        Assert.Equal("abcd efgh ijkl mnop", _credentials.Secrets["AutoMailChecker/gmail1"]);
        Assert.Equal("登録済み", viewModel.GeneralTab.Accounts[1].PasswordStatus);
        Assert.DoesNotContain("abcd", ConfigJson.Serialize(viewModel.BuildConfig()));
    }

    [Fact]
    public async Task AI_の接続先_URL_を保存し_ほかの_PC_は赤枠にする()
    {
        var viewModel = Create();
        Assert.Equal("http://127.0.0.1:8080", viewModel.GeneralTab.EndpointText);
        Assert.False(viewModel.GeneralTab.UsesModelFile);

        viewModel.GeneralTab.EndpointText = "http://192.168.1.20:8080";
        await SaveAsync(viewModel);
        Assert.Null(_config.Saved);
        Assert.True(viewModel.GeneralTab.GetErrors(nameof(GeneralTabViewModel.EndpointText)).Cast<string>().Any());
        Assert.Contains(viewModel.Issues, i => i.StartsWith("AI の接続先 URL: ", StringComparison.Ordinal));

        viewModel.GeneralTab.EndpointText = " http://localhost:8081 ";
        await SaveAsync(viewModel, afterFailure: true);
        Assert.Equal("http://localhost:8081", _config.Saved?.Llm.Endpoint);
    }

    [Fact]
    public void 接続先_URL_を空にするとモデルファイルを使う()
    {
        var viewModel = Create();

        viewModel.GeneralTab.EndpointText = "";

        Assert.True(viewModel.GeneralTab.UsesModelFile);
        Assert.False(viewModel.BuildConfig().Llm.UsesEndpoint);
    }

    [Fact]
    public void API_キーは資格情報マネージャーにだけ登録する()
    {
        var viewModel = Create();
        Assert.StartsWith("API キー: 未登録", viewModel.GeneralTab.ApiKeyStatus);
        _dialogs.ApiKey = "sk-local-123";

        viewModel.GeneralTab.RegisterApiKeyCommand.Execute(null);

        Assert.Equal("sk-local-123", _credentials.Secrets[LlmEndpoint.ApiKeyCredentialKey]);
        Assert.Equal("API キー: 登録済み", viewModel.GeneralTab.ApiKeyStatus);
        Assert.DoesNotContain("sk-local", ConfigJson.Serialize(viewModel.BuildConfig()));
    }

    [Fact]
    public async Task AI_の接続テストは保存前の_URL_で行い_結果を出す()
    {
        var viewModel = Create();

        viewModel.GeneralTab.TestLlmCommand.Execute(null);
        await WaitForAsync(() => viewModel.GeneralTab.LlmTestResult?.StartsWith("接続できました", StringComparison.Ordinal) == true);

        viewModel.GeneralTab.EndpointText = "http://127.0.0.1:9999";
        viewModel.GeneralTab.TestLlmCommand.Execute(null);
        await WaitForAsync(() => viewModel.GeneralTab.LlmTestResult?.Contains("繋がりません", StringComparison.Ordinal) == true);

        Assert.Equal(["http://127.0.0.1:8080", "http://127.0.0.1:9999"], _llmTester.Endpoints);
    }

    [Fact]
    public void 自動起動を切り替えるとすぐタスクスケジューラに登録_削除する()
    {
        var viewModel = Create();

        viewModel.GeneralTab.AutoStartEnabled = true;
        Assert.True(_autoStart.Registered);

        viewModel.GeneralTab.AutoStartEnabled = false;
        Assert.False(_autoStart.Registered);
    }

    [Theory]
    [InlineData(RuleMethod.Exe, new[] { "javaw.exe" }, null)]
    [InlineData(RuleMethod.Title, new string[0], "Minecraft")]
    [InlineData(RuleMethod.Both, new[] { "javaw.exe" }, "Minecraft")]
    public void 停止対象は判定方法に応じて実行ファイル名とタイトルを使う(RuleMethod method, string[] exe, string? title)
    {
        var rule = new PauseRuleViewModel(new PauseAppRule { Name = "Minecraft", Exe = ["javaw.exe"], TitleContains = "Minecraft" })
        {
            Method = method,
        };

        var built = rule.ToRule();

        Assert.Equal(exe, built.Exe);
        Assert.Equal(title, built.TitleContains);
    }

    [Fact]
    public void 停止対象タブに現在の検知を出す()
    {
        var runModes = new FakeRunModeController { Detections = [new PauseDetection("app:Roblox", PauseAction.Stop, "Roblox起動中")] };
        var viewModel = Create(runModes);

        Assert.Equal("現在の検知: Roblox起動中", viewModel.PauseTab.CurrentDetection);
    }

    private SettingsViewModel Create(FakeRunModeController? runModes = null) => new(
        new SettingsServices(_config, _catalog, new FakeRejudge(), _store, runModes ?? new FakeRunModeController(),
            new EmptySystem(), _credentials, new FakeTester(), _llmTester, _autoStart, _temp.Path),
        _dialogs, SettingsTab.Items);

    /// <param name="afterFailure">前回の保存が誤りで止まっていたら true（今度は保存できるまで待つ）</param>
    private static async Task SaveAsync(SettingsViewModel viewModel, bool afterFailure = false)
    {
        viewModel.SaveCommand.Execute(null);
        await WaitForAsync(() => afterFailure ? !viewModel.StatusIsError : viewModel.StatusText is not null);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeDialogs : ISettingsDialogs
    {
        public bool ConfirmResult { get; set; } = true;
        public string? Password { get; set; }

        public bool Confirm(string message) => ConfirmResult;
        public string? AskPassword(string accountId, string user) => Password;
        public string? ApiKey { get; set; }
        public string? AskApiKey() => ApiKey;
        public string? PickExecutable() => null;
        public string? PickFolder(string? initialFolder) => null;
        public string? PickModelFile(string? current) => null;
        public ProcessInfo? PickRunningApp(IReadOnlyList<ProcessInfo> processes) => null;
        public void ShowRejudge(RejudgeViewModel viewModel) { }
        public void OpenFolder(string path) { }
    }

    private sealed class FakeCatalog : IMailFolderCatalog
    {
        public Dictionary<string, IReadOnlyList<string>> Folders { get; } = [];

        public Task<IReadOnlyList<string>> GetSortingFoldersAsync(string accountId, bool refresh, CancellationToken ct) =>
            Folders.TryGetValue(accountId, out var folders)
                ? Task.FromResult(folders)
                : throw new MailServerException(accountId, "パスワードが登録されていません。", isAuthenticationFailure: true);
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        public Dictionary<string, string> Secrets { get; } = [];
        public string? ReadSecret(string key) => Secrets.GetValueOrDefault(key);
        public void WriteSecret(string key, string userName, string secret) => Secrets[key] = secret;
    }

    private sealed class FakeAutoStart : IAutoStartRegistrar
    {
        public bool Registered { get; private set; }
        public bool IsRegistered() => Registered;
        public void Register() => Registered = true;
        public void Unregister() => Registered = false;
    }

    private sealed class FakeTester : IMailConnectionTester
    {
        public Task<string> TestAsync(AccountConfig account, CancellationToken ct) => Task.FromResult("接続できました。");
    }

    private sealed class FakeLlmTester : ILlmConnectionTester
    {
        public List<string> Endpoints { get; } = [];

        public Task<string> TestAsync(string endpoint, CancellationToken ct)
        {
            Endpoints.Add(endpoint);
            return endpoint.Contains("8080", StringComparison.Ordinal)
                ? Task.FromResult("接続できました（モデル: test-model）。")
                : throw new LlmStartException("http://127.0.0.1:9999 に繋がりません。");
        }
    }

    private sealed class FakeRejudge : IRejudgeService
    {
        public bool IsAvailable => true;
        public Task<RejudgeResult> RejudgeAsync(long judgementId, AppConfig draft, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeRunModeController : IRunModeController
    {
        public IReadOnlyList<PauseDetection> Detections { get; init; } = [];
        public RunModeState Current => RunModeState.Running;
        public IReadOnlyList<PauseDetection> CurrentDetections => Detections;

        public event EventHandler<RunModeState>? Changed
        {
            add { }
            remove { }
        }

        public void PauseFor(TimeSpan? duration) { }
        public void Stop() { }
        public void Resume() { }
    }

    private sealed class EmptySystem : ISystemSnapshotSource
    {
        public IReadOnlyList<ProcessInfo> GetProcesses() => [];
        public IReadOnlyList<WindowInfo> GetWindows() => [];
        public bool IsFullscreen() => false;
    }
}
