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
    private Func<string>? _statusText;

    /// <summary>いま出している誤りと注意を、今の言語で出し直す（言語を切り替えたときに使う）。</summary>
    private Action? _reshowIssues;
    private bool _statusIsError;

    public SettingsViewModel(SettingsServices services, ISettingsDialogs dialogs, SettingsTab tab)
    {
        _config = services.Config;
        var current = services.Config.Current;
        ItemsTab = new ItemsTabViewModel(current, services.Folders, services.Rejudge, services.Store, dialogs, BuildConfig);
        PauseTab = new PauseTabViewModel(current.Pause, services.RunModes, services.System, dialogs);
        GeneralTab = new GeneralTabViewModel(current, services.Credentials, services.ConnectionTester, services.LlmTester,
            services.AutoStart, services.Store, dialogs, services.LogDirectory, SaveLanguageAsync);
        _selectedTabIndex = (int)tab;

        // 全般タブでアカウントを増減したら、項目タブの振り分け先の欄も合わせる
        GeneralTab.Accounts.CollectionChanged += OnAccountsChanged;
        foreach (var account in GeneralTab.Accounts)
        {
            account.PropertyChanged += OnAccountChanged;
        }

        SaveCommand = new AsyncCommand(SaveAsync, onError: ex => ShowStatus(() => Translate.Text(
            $"保存できませんでした: {ex.Message}",
            $"Could not save: {ex.Message}"), isError: true));
    }

    public ItemsTabViewModel ItemsTab { get; }
    public PauseTabViewModel PauseTab { get; }
    public GeneralTabViewModel GeneralTab { get; }
    public ICommand SaveCommand { get; }

    /// <summary>誤りと警告の一覧（保存ボタンの上に出す）。</summary>
    public ObservableCollection<string> Issues { get; } = [];

    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    /// <summary>保存の結果。文言は読むたびに作る（言語を切り替えたら読み直す）。</summary>
    public string? StatusText => _statusText?.Invoke();
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
        _reshowIssues = null;
        ClearIssues();

        // 数字の欄が数字でなければ、保存せずにその欄を示す
        if (ShowNumberErrors() > 0)
        {
            _reshowIssues = () => ShowNumberErrors();
            return;
        }

        var config = BuildConfig();
        var result = await _config.SaveAsync(config, CancellationToken.None);
        if (!result.Saved)
        {
            ShowIssues(result.Issues);
            _reshowIssues = () => ShowIssues(ConfigValidator.Validate(config));
            return;
        }

        ShowWarnings();
        _reshowIssues = ShowWarnings;

        ShowStatus(() => result.RestartRequired
            ? Translate.Text(
                "保存しました。アカウントの追加・削除と AI の接続先・モデルの変更は、アプリを再起動すると反映されます。",
                "Saved. Added or removed accounts and changes to the AI server or model take effect after you restart the app.")
            : Translate.Text("保存しました。すぐに反映されます。", "Saved. The changes take effect right away."), isError: false);
        foreach (var account in GeneralTab.Accounts)
        {
            account.RefreshPasswordStatus();
        }
    }

    protected override void OnLanguageChanged()
    {
        _reshowIssues?.Invoke();
        base.OnLanguageChanged();
    }

    private void ClearIssues()
    {
        ItemsTab.ClearErrors();
        PauseTab.ClearErrors();
        GeneralTab.ClearErrors();
        Issues.Clear();
    }

    private int ShowNumberErrors()
    {
        ClearIssues();
        var count = 0;
        count += CheckNumber(GeneralTab, nameof(GeneralTab.RetentionDaysText), GeneralTab.RetentionDaysText, Translate.Text(
            "判定履歴の保持日数",
            "Days to keep judgment history"));
        count += CheckNumber(PauseTab, nameof(PauseTab.ResumeDelayText), PauseTab.ResumeDelayText, Translate.Text(
            "再開までの待ち時間",
            "Wait before resuming"));
        if (count > 0)
        {
            ShowStatus(() => Translate.Text($"保存できませんでした（誤り {count} 件）。", $"Could not save ({Problems(count)})."), isError: true);
        }

        return count;
    }

    private void ShowIssues(IReadOnlyList<ConfigIssue> issues)
    {
        ClearIssues();
        foreach (var issue in issues)
        {
            Issues.Add(ApplyIssue(issue));
        }

        ShowStatus(() => Translate.Text(
            $"保存できませんでした（誤り {issues.Count} 件）。赤枠の欄を直してください。",
            $"Could not save ({Problems(issues.Count)}). Fix the fields outlined in red."), isError: true);
    }

    private void ShowWarnings()
    {
        Issues.Clear();
        foreach (var warning in ItemsTab.FolderWarnings())
        {
            Issues.Add(Translate.Text($"注意: {warning}", $"Note: {warning}"));
        }
    }

    private static string Problems(int count) => count == 1 ? "1 problem" : $"{count} problems";

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
                    var itemName = string.IsNullOrWhiteSpace(item.Label) ? Translate.Text(
                        $"{index + 1} 番目の項目",
                        $"Item {index + 1}") : Translate.Text($"項目「{item.Label}」", $"Item \"{item.Label}\"");
                    if (field == "folders" && item.Folders.FirstOrDefault(f => f.AccountId == key) is { } folder)
                    {
                        folder.AddError(nameof(FolderChoice.Folder), message);
                        return Translate.Text($"{itemName}の {key} の振り分け先: {message}", $"{itemName}, destination for {key}: {message}");
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
                        ? Translate.Text($"{index + 1} 番目のアカウント: {message}", $"Account {index + 1}: {message}")
                        : Translate.Text($"アカウント「{account.Id}」: {message}", $"Account \"{account.Id}\": {message}");

                case "pause.apps" when index < PauseTab.Rules.Count:
                    var rule = PauseTab.Rules[index];
                    rule.AddError(field switch
                    {
                        "exe" => nameof(PauseRuleViewModel.ExeText),
                        _ => nameof(PauseRuleViewModel.Name),
                    }, message);
                    return string.IsNullOrWhiteSpace(rule.Name)
                        ? Translate.Text($"{index + 1} 番目の停止対象: {message}", $"Pause rule {index + 1}: {message}")
                        : Translate.Text($"停止対象「{rule.Name}」: {message}", $"Pause rule \"{rule.Name}\": {message}");
            }
        }

        switch (path)
        {
            case "history.retentionDays":
                GeneralTab.AddError(nameof(GeneralTab.RetentionDaysText), message);
                return Translate.Text($"判定履歴の保持日数: {message}", $"Days to keep judgment history: {message}");
            case "pause.resumeDelaySeconds":
                PauseTab.AddError(nameof(PauseTab.ResumeDelayText), message);
                return Translate.Text($"再開までの待ち時間: {message}", $"Wait before resuming: {message}");
            case "llm.endpoint":
                GeneralTab.AddError(nameof(GeneralTab.EndpointText), message);
                return Translate.Text($"AI の接続先 URL: {message}", $"AI server URL: {message}");
            case "llm.model":
                GeneralTab.AddError(nameof(GeneralTab.ModelPath), message);
                return Translate.Text($"モデルファイル: {message}", $"Model file: {message}");
        }

        return issue.ToString();
    }

    private static int CheckNumber(EditableObject owner, string property, string text, string label)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return 0;
        }

        owner.AddError(property, Translate.Text($"{label}は数字で指定してください。", $"{label} must be a number."));
        return 1;
    }

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void ShowStatus(Func<string> text, bool isError)
    {
        _statusText = text;
        OnPropertyChanged(nameof(StatusText));
        StatusIsError = isError;
    }

    /// <summary>
    /// 全般タブで言語を選んだら、すぐ設定ファイルに保存する（ほかの欄の入力中の内容は保存しない）。
    /// 設定ファイルに誤りがあるときは、書き戻すと手で直した内容が消えるので保存しない。
    /// </summary>
    private async Task SaveLanguageAsync(UiLanguage language)
    {
        var current = _config.Current;
        if (current.Ui.Language == language)
        {
            return;
        }

        if (_config.Issues.Count > 0)
        {
            throw new InvalidOperationException(Translate.Text(
                "設定ファイルに誤りがあるため保存しませんでした。",
                "The settings file has errors, so it was not saved."));
        }

        var result = await _config.SaveAsync(current with { Ui = current.Ui with { Language = language } }, CancellationToken.None);
        if (!result.Saved)
        {
            throw new InvalidOperationException(string.Join(" ", result.Issues.Select(i => i.ToString())));
        }
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
