using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.Pause;
using AutoMailChecker.Windows.Startup;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.Settings;

public enum SettingsTab
{
    Items,
    Pause,
    General,
}

/// <summary>設定ウィンドウが使うサービス。</summary>
public sealed record SettingsServices(
    IConfigProvider Config,
    IMailFolderCatalog Folders,
    IRejudgeService Rejudge,
    IStateStore Store,
    IRunModeController RunModes,
    ISystemSnapshotSource System,
    ICredentialStore Credentials,
    IMailConnectionTester ConnectionTester,
    ILlmConnectionTester LlmTester,
    IAutoStartRegistrar AutoStart,
    string LogDirectory);

/// <summary>
/// 設定ウィンドウ（画面設計 SC-02〜04）。3つのタブは保存ボタンを共有し、保存時に検証する。
/// 誤りがあれば保存せず、該当欄を赤枠にして理由を出す。保存した内容は config.jsonc に書き戻し、再起動なしで反映される。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigProvider _config;
    private int _selectedTabIndex;
    private string? _statusText;
    private bool _statusIsError;

    public SettingsViewModel(SettingsServices services, ISettingsDialogs dialogs, SettingsTab tab)
    {
        _config = services.Config;
        var current = services.Config.Current;
        ItemsTab = new ItemsTabViewModel(current, services.Folders, services.Rejudge, services.Store, dialogs, BuildConfig);
        PauseTab = new PauseTabViewModel(current.Pause, services.RunModes, services.System, dialogs);
        GeneralTab = new GeneralTabViewModel(current, services.Credentials, services.ConnectionTester, services.LlmTester,
            services.AutoStart, services.Store, dialogs, services.LogDirectory);
        _selectedTabIndex = (int)tab;

        // 全般タブでアカウントを増減したら、項目タブの振り分け先の欄も合わせる
        GeneralTab.Accounts.CollectionChanged += OnAccountsChanged;
        foreach (var account in GeneralTab.Accounts)
        {
            account.PropertyChanged += OnAccountChanged;
        }

        SaveCommand = new AsyncCommand(SaveAsync, onError: ex => ShowStatus($"保存できませんでした: {ex.Message}", isError: true));
    }

    public ItemsTabViewModel ItemsTab { get; }
    public PauseTabViewModel PauseTab { get; }
    public GeneralTabViewModel GeneralTab { get; }
    public ICommand SaveCommand { get; }

    /// <summary>誤りと警告の一覧（保存ボタンの上に出す）。</summary>
    public ObservableCollection<string> Issues { get; } = [];

    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

    public async Task InitializeAsync()
    {
        await GeneralTab.InitializeAsync();
        await ItemsTab.LoadFoldersAsync(refresh: false);
    }

    /// <summary>画面の内容から設定を組み立てる（試し判定は保存前のこの設定で判定する）。画面にない設定は今の値のまま。</summary>
    public AppConfig BuildConfig()
    {
        var current = _config.Current;
        return current with
        {
            Accounts = GeneralTab.BuildAccounts(),
            Items = ItemsTab.BuildItems(),
            Advertisement = ItemsTab.BuildAdvertisement(),
            Policy = current.Policy with
            {
                AutoSort = GeneralTab.AutoSort,
                MinConfidence = GeneralTab.HighConfidenceOnly ? Confidence.High : Confidence.Medium,
                ErrorPopup = GeneralTab.ErrorPopup,
            },
            History = current.History with { RetentionDays = ParseOr(GeneralTab.RetentionDaysText, current.History.RetentionDays) },
            Llm = current.Llm with { Endpoint = GeneralTab.EndpointText.Trim(), Model = GeneralTab.ModelPath.Trim() },
            Pause = current.Pause with
            {
                DmmGameFolder = string.IsNullOrWhiteSpace(PauseTab.DmmGameFolder) ? null : PauseTab.DmmGameFolder.Trim(),
                Apps = PauseTab.BuildRules(),
                FullscreenDetect = PauseTab.FullscreenDetect,
                ResumeDelaySeconds = ParseOr(PauseTab.ResumeDelayText, current.Pause.ResumeDelaySeconds),
            },
        };
    }

    private async Task SaveAsync()
    {
        ItemsTab.ClearErrors();
        PauseTab.ClearErrors();
        GeneralTab.ClearErrors();
        Issues.Clear();

        // 数字の欄が数字でなければ、保存せずにその欄を示す
        var numberErrors = 0;
        numberErrors += CheckNumber(GeneralTab, nameof(GeneralTab.RetentionDaysText), GeneralTab.RetentionDaysText, "判定履歴の保持日数");
        numberErrors += CheckNumber(PauseTab, nameof(PauseTab.ResumeDelayText), PauseTab.ResumeDelayText, "再開までの待ち時間");
        if (numberErrors > 0)
        {
            ShowStatus($"保存できませんでした（誤り {numberErrors} 件）。", isError: true);
            return;
        }

        var config = BuildConfig();
        var result = await _config.SaveAsync(config, CancellationToken.None);
        if (!result.Saved)
        {
            foreach (var issue in result.Issues)
            {
                Issues.Add(ApplyIssue(issue));
            }

            ShowStatus($"保存できませんでした（誤り {result.Issues.Count} 件）。赤枠の欄を直してください。", isError: true);
            return;
        }

        foreach (var warning in ItemsTab.FolderWarnings())
        {
            Issues.Add($"注意: {warning}");
        }

        ShowStatus(result.RestartRequired
            ? "保存しました。アカウントの追加・削除と AI の接続先・モデルの変更は、アプリを再起動すると反映されます。"
            : "保存しました。すぐに反映されます。", isError: false);
        foreach (var account in GeneralTab.Accounts)
        {
            account.RefreshPasswordStatus();
        }
    }

    /// <summary>誤りを該当欄に付け、一覧に出す文言を返す。</summary>
    private string ApplyIssue(ConfigIssue issue)
    {
        var message = issue.Message;
        var path = issue.Path;

        if (IndexedPath().Match(path) is { Success: true } match)
        {
            var list = match.Groups["list"].Value;
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            var field = match.Groups["field"].Value;
            var key = match.Groups["key"].Value;
            switch (list)
            {
                case "items" when index < ItemsTab.Items.Count:
                    var item = ItemsTab.Items[index];
                    // 表示名が空のときは何番目かで示す
                    var itemName = string.IsNullOrWhiteSpace(item.Label) ? $"{index + 1} 番目の項目" : $"項目「{item.Label}」";
                    if (field == "folders" && item.Folders.FirstOrDefault(f => f.AccountId == key) is { } folder)
                    {
                        folder.AddError(nameof(FolderChoice.Folder), message);
                        return $"{itemName}の {key} の振り分け先: {message}";
                    }

                    item.AddError(field switch
                    {
                        "description" => nameof(ItemEditViewModel.Description),
                        "examples" => nameof(ItemEditViewModel.ExamplesText),
                        "counterExamples" => nameof(ItemEditViewModel.CounterExamplesText),
                        _ => nameof(ItemEditViewModel.Label),
                    }, message);
                    return $"{itemName}: {message}";

                case "accounts" when index < GeneralTab.Accounts.Count:
                    var account = GeneralTab.Accounts[index];
                    account.AddError(field == "id" ? nameof(AccountEditViewModel.Id) : nameof(AccountEditViewModel.User), message);
                    return string.IsNullOrWhiteSpace(account.Id)
                        ? $"{index + 1} 番目のアカウント: {message}"
                        : $"アカウント「{account.Id}」: {message}";

                case "pause.apps" when index < PauseTab.Rules.Count:
                    var rule = PauseTab.Rules[index];
                    rule.AddError(field switch
                    {
                        "exe" => nameof(PauseRuleViewModel.ExeText),
                        _ => nameof(PauseRuleViewModel.Name),
                    }, message);
                    return string.IsNullOrWhiteSpace(rule.Name)
                        ? $"{index + 1} 番目の停止対象: {message}"
                        : $"停止対象「{rule.Name}」: {message}";
            }
        }

        if (path.StartsWith("advertisement.folders.", StringComparison.Ordinal))
        {
            var accountId = path["advertisement.folders.".Length..];
            ItemsTab.AdFolders.FirstOrDefault(f => f.AccountId == accountId)?.AddError(nameof(FolderChoice.Folder), message);
            return $"広告用フォルダー（{accountId}）: {message}";
        }

        switch (path)
        {
            case "history.retentionDays":
                GeneralTab.AddError(nameof(GeneralTab.RetentionDaysText), message);
                return $"判定履歴の保持日数: {message}";
            case "pause.resumeDelaySeconds":
                PauseTab.AddError(nameof(PauseTab.ResumeDelayText), message);
                return $"再開までの待ち時間: {message}";
            case "llm.endpoint":
                GeneralTab.AddError(nameof(GeneralTab.EndpointText), message);
                return $"AI の接続先 URL: {message}";
            case "llm.model":
                GeneralTab.AddError(nameof(GeneralTab.ModelPath), message);
                return $"モデルファイル: {message}";
        }

        return issue.ToString();
    }

    private static int CheckNumber(EditableObject owner, string property, string text, string label)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return 0;
        }

        owner.AddError(property, $"{label}は数字で指定してください。");
        return 1;
    }

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void ShowStatus(string text, bool isError)
    {
        StatusText = text;
        StatusIsError = isError;
    }

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var added in e.NewItems?.OfType<AccountEditViewModel>() ?? [])
        {
            added.PropertyChanged += OnAccountChanged;
        }

        SyncAccounts();
    }

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountEditViewModel.Id))
        {
            SyncAccounts();
        }
    }

    private void SyncAccounts() =>
        ItemsTab.SyncAccounts(GeneralTab.Accounts.Select(a => a.Id).Where(id => id.Length > 0).Distinct().ToList());

    // 例: items[2].label / items[0].folders.gmail1 / accounts[1].id / pause.apps[3].exe
    [GeneratedRegex(@"^(?<list>items|accounts|pause\.apps)\[(?<index>\d+)\](?:\.(?<field>[A-Za-z]+)(?:\.(?<key>[^.\[]+)|\[\d+\])?)?$")]
    private static partial Regex IndexedPath();
}
