using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Core;
using AutoMailChecker.Windows.Startup;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.Settings;

/// <summary>アカウント1つ（画面設計 SC-04）。パスワードは資格情報マネージャーにだけ保存し、画面にも設定ファイルにも出さない。</summary>
public sealed class AccountEditViewModel : EditableObject
{
    private readonly AccountConfig _original;
    private readonly ICredentialStore _credentials;
    private string _id;
    private string _user;
    private bool? _hasPassword;
    private string? _testResult;

    public AccountEditViewModel(AccountConfig original, bool isNew, ICredentialStore credentials)
    {
        _original = original;
        _credentials = credentials;
        _id = original.Id;
        _user = original.User;
        IsNew = isNew;
        RefreshPasswordStatus();
    }

    /// <summary>新しく追加したアカウントだけ ID を変えられる（判定履歴との対応を保つため）。</summary>
    public bool IsNew { get; }

    public string Id
    {
        get => _id;
        set
        {
            if (IsNew && SetProperty(ref _id, value))
            {
                RefreshPasswordStatus();
            }
        }
    }

    public string Kind => _original.Host switch
    {
        "imap.mail.me.com" => "iCloud",
        "imap.gmail.com" => "Gmail",
        var host => host,
    };

    public string Host => _original.Host;
    public string User { get => _user; set => SetProperty(ref _user, value); }
    public string PasswordStatus => _hasPassword switch
    {
        true => Translate.Text("登録済み", "Registered"),
        false => Translate.Text("未登録", "Not registered"),
        null => Translate.Text("確認できません", "Unknown"),
    };
    public string? TestResult { get => _testResult; set => SetProperty(ref _testResult, value); }

    public AccountConfig ToConfig() => _original with
    {
        Id = Id.Trim(),
        User = User.Trim(),
        // 新しいアカウントは既定の名前（AutoMailChecker/＜id＞）を使う。既存のアカウントは設定ファイルの名前のまま
        CredentialKey = IsNew ? null : _original.CredentialKey,
    };

    public void RefreshPasswordStatus()
    {
        try
        {
            _hasPassword = _credentials.ReadSecret(ToConfig().EffectiveCredentialKey) is { Length: > 0 };
        }
        catch (Win32Exception)
        {
            _hasPassword = null;
        }

        OnPropertyChanged(nameof(PasswordStatus));
    }
}

/// <summary>
/// 設定ウィンドウの「全般」タブ（画面設計 SC-04）。アカウント、自動仕分け、確信度のしきい値、ポップアップ、履歴の保持日数、
/// AI の接続先 URL（空ならモデルファイル）、Windows のログオン時の自動起動。
/// </summary>
public sealed class GeneralTabViewModel : EditableObject
{
    private readonly ICredentialStore _credentials;
    private readonly IMailConnectionTester _tester;
    private readonly ILlmConnectionTester _llmTester;
    private readonly IAutoStartRegistrar _autoStart;
    private readonly IStateStore _store;
    private readonly ISettingsDialogs _dialogs;
    private readonly string _logDirectory;
    private readonly Func<UiLanguage, Task> _saveLanguage;
    private AccountEditViewModel? _selectedAccount;
    private bool _autoSort;
    private bool _highConfidenceOnly;
    private bool _errorPopup;
    private string _retentionDaysText;
    private string _endpointText;
    private string _modelPath;
    private bool? _hasApiKey;
    private Func<string>? _llmTestResult;
    private bool _autoStartEnabled;
    private Func<string>? _autoStartMessage;
    private Choice<UiLanguage> _selectedLanguage;
    private Func<string>? _languageMessage;
    private bool _hasCompletedTrial;

    public GeneralTabViewModel(
        AppConfig config, ICredentialStore credentials, IMailConnectionTester tester, ILlmConnectionTester llmTester,
        IAutoStartRegistrar autoStart, IStateStore store, ISettingsDialogs dialogs, string logDirectory,
        Func<UiLanguage, Task> saveLanguage)
    {
        _saveLanguage = saveLanguage;
        _credentials = credentials;
        _tester = tester;
        _llmTester = llmTester;
        _autoStart = autoStart;
        _store = store;
        _dialogs = dialogs;
        _logDirectory = logDirectory;
        Accounts = new ObservableCollection<AccountEditViewModel>(
            config.Accounts.Select(a => new AccountEditViewModel(a, isNew: false, credentials)));
        SelectedAccount = Accounts.FirstOrDefault();
        _autoSort = config.Policy.AutoSort;
        _highConfidenceOnly = config.Policy.MinConfidence == Confidence.High;
        _errorPopup = config.Policy.ErrorPopup;
        _retentionDaysText = config.History.RetentionDays.ToString(CultureInfo.InvariantCulture);
        _endpointText = config.Llm.Endpoint;
        _modelPath = config.Llm.Model;
        RefreshApiKeyStatus();
        _selectedLanguage = LanguageChoices.FirstOrDefault(c => c.Value == config.Ui.Language) ?? LanguageChoices[0];

        AddICloudCommand = new RelayCommand(() => AddAccount("icloud", "imap.mail.me.com"));
        AddGmailCommand = new RelayCommand(() => AddAccount("gmail", "imap.gmail.com"));
        RemoveAccountCommand = new RelayCommand(RemoveAccount, () => SelectedAccount is not null);
        RegisterPasswordCommand = new RelayCommand(RegisterPassword, () => SelectedAccount is not null);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => SelectedAccount is not null);
        RegisterApiKeyCommand = new RelayCommand(RegisterApiKey);
        TestLlmCommand = new AsyncCommand(TestLlmAsync);
        BrowseModelCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickModelFile(ModelPath) is { } path)
            {
                ModelPath = path;
            }
        });
        OpenLogFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_logDirectory));
    }

    public ObservableCollection<AccountEditViewModel> Accounts { get; }

    public ICommand AddICloudCommand { get; }
    public ICommand AddGmailCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand RegisterPasswordCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand RegisterApiKeyCommand { get; }
    public ICommand TestLlmCommand { get; }
    public ICommand BrowseModelCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public AccountEditViewModel? SelectedAccount { get => _selectedAccount; set => SetProperty(ref _selectedAccount, value); }

    /// <summary>新着の自動仕分け。お試し仕分けを1回以上行うまでは、オンにするとき確認を出す。</summary>
    public bool AutoSort
    {
        get => _autoSort;
        set
        {
            if (value && !_autoSort && !_hasCompletedTrial && !_dialogs.Confirm(
                    Translate.Text(
                        "まだお試し仕分けを行っていません。\n新着メールの自動仕分けを始めると、届いたメールが実際に振り分け先へ移動します。\nオンにしますか？",
                        "You have not run a trial sort yet.\nOnce automatic sorting starts, incoming mail is actually moved to its destination.\nTurn it on?")))
            {
                OnPropertyChanged(); // チェックを戻す
                return;
            }

            SetProperty(ref _autoSort, value);
        }
    }

    /// <summary>移動に必要な確信度。true なら「高のみ」、false なら「中以上」（既定）。</summary>
    public bool HighConfidenceOnly
    {
        get => _highConfidenceOnly;
        set
        {
            if (SetProperty(ref _highConfidenceOnly, value))
            {
                OnPropertyChanged(nameof(MediumOrAbove));
            }
        }
    }

    public bool MediumOrAbove { get => !HighConfidenceOnly; set => HighConfidenceOnly = !value; }

    public bool ErrorPopup { get => _errorPopup; set => SetProperty(ref _errorPopup, value); }

    /// <summary>判定履歴の保持日数（7〜365、既定 90）。</summary>
    public string RetentionDaysText { get => _retentionDaysText; set => SetProperty(ref _retentionDaysText, value); }

    /// <summary>起動済みの AI サーバーの接続先 URL。空ならモデルファイルで llama-server を起動する。変更は再起動が必要。</summary>
    public string EndpointText
    {
        get => _endpointText;
        set
        {
            if (SetProperty(ref _endpointText, value))
            {
                OnPropertyChanged(nameof(UsesModelFile));
            }
        }
    }

    /// <summary>接続先 URL が空なら、モデルファイルを使う（アプリが llama-server を起動する）。</summary>
    public bool UsesModelFile => string.IsNullOrWhiteSpace(EndpointText);

    /// <summary>モデルファイル。接続先 URL が空のときだけ使う。変更は再起動が必要。</summary>
    public string ModelPath { get => _modelPath; set => SetProperty(ref _modelPath, value); }

    public string ApiKeyStatus => _hasApiKey switch
    {
        true => Translate.Text("API キー: 登録済み", "API key: registered"),
        false => Translate.Text(
            "API キー: 未登録（サーバーを --api-key 付きで起動したときだけ必要です）",
            "API key: not registered (needed only if the server was started with --api-key)"),
        null => Translate.Text("API キー: 確認できません", "API key: unknown"),
    };

    public string? LlmTestResult => _llmTestResult?.Invoke();

    /// <summary>Windows のログオン時に自動で起動する（タスクスケジューラ）。切り替えるとすぐ登録・削除する。</summary>
    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        set
        {
            if (value == _autoStartEnabled)
            {
                return;
            }

            try
            {
                if (value)
                {
                    _autoStart.Register();
                    SetAutoStartMessage(() => Translate.Text(
                        "タスクスケジューラに登録しました。次回のログオンから自動で起動します。",
                        "Registered in Task Scheduler. The app starts automatically from your next sign-in."));
                }
                else
                {
                    _autoStart.Unregister();
                    SetAutoStartMessage(() => Translate.Text("タスクスケジューラから削除しました。", "Removed from Task Scheduler."));
                }

                _autoStartEnabled = value;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
            {
                SetAutoStartMessage(() => ex.Message);
            }

            OnPropertyChanged();
        }
    }

    public string? AutoStartMessage => _autoStartMessage?.Invoke();

    /// <summary>画面の言語の選択肢。表示名はそれぞれの言語で書く（どちらの言語でも読めるように）。</summary>
    public IReadOnlyList<Choice<UiLanguage>> LanguageChoices { get; } =
    [
        new("日本語", UiLanguage.Japanese),
        new("English", UiLanguage.English),
    ];

    /// <summary>画面の言語。選ぶとすぐ切り替え、設定ファイルに保存する（ほかの欄の入力中の内容はそのまま）。</summary>
    public Choice<UiLanguage> SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !SetProperty(ref _selectedLanguage, value))
            {
                return;
            }

            Translate.SetLanguage(value.Value);
            _ = SaveLanguageAsync(value.Value);
        }
    }

    /// <summary>言語を保存できなかったときの知らせ。</summary>
    public string? LanguageMessage => _languageMessage?.Invoke();

    /// <summary>画面を開いたとき。自動起動の登録状況と、お試し仕分けを行ったことがあるかを調べる。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _autoStartEnabled = await Task.Run(_autoStart.IsRegistered);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
        {
            SetAutoStartMessage(() => Translate.Text(
                $"自動起動の登録状況を確認できません: {ex.Message}", $"Cannot check the automatic start registration: {ex.Message}"));
        }

        OnPropertyChanged(nameof(AutoStartEnabled));
        _hasCompletedTrial = (await _store.TrialRuns.ListAsync(CancellationToken.None)).Any(r => r.Status == TrialRunStatus.Done);
    }

    public IReadOnlyList<AccountConfig> BuildAccounts() => Accounts.Select(a => a.ToConfig()).ToList();

    public new void ClearErrors()
    {
        base.ClearErrors();
        foreach (var account in Accounts)
        {
            account.ClearErrors();
        }
    }

    private void RefreshApiKeyStatus()
    {
        try
        {
            _hasApiKey = _credentials.ReadSecret(LlmEndpoint.ApiKeyCredentialKey) is { Length: > 0 };
        }
        catch (Win32Exception)
        {
            _hasApiKey = null;
        }

        OnPropertyChanged(nameof(ApiKeyStatus));
    }

    private async Task SaveLanguageAsync(UiLanguage language)
    {
        try
        {
            await _saveLanguage(language);
            SetLanguageMessage(null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetLanguageMessage(() => Translate.Text(
                $"言語を設定ファイルに保存できませんでした（次回の起動では元の言語に戻ります）: {ex.Message}",
                $"Could not save the language to the settings file (it goes back to the previous language at the next start): {ex.Message}"));
        }
    }

    private void SetAutoStartMessage(Func<string>? message)
    {
        _autoStartMessage = message;
        OnPropertyChanged(nameof(AutoStartMessage));
    }

    private void SetLlmTestResult(Func<string>? message)
    {
        _llmTestResult = message;
        OnPropertyChanged(nameof(LlmTestResult));
    }

    private void SetLanguageMessage(Func<string>? message)
    {
        _languageMessage = message;
        OnPropertyChanged(nameof(LanguageMessage));
    }

    private void RegisterApiKey()
    {
        if (_dialogs.AskApiKey() is { Length: > 0 } apiKey)
        {
            _credentials.WriteSecret(LlmEndpoint.ApiKeyCredentialKey, "api-key", apiKey);
            RefreshApiKeyStatus();
            SetLlmTestResult(() => Translate.Text(
                $"API キーを資格情報マネージャー（{LlmEndpoint.ApiKeyCredentialKey}）に登録しました。",
                $"Registered the API key in Credential Manager ({LlmEndpoint.ApiKeyCredentialKey})."));
        }
    }

    private async Task TestLlmAsync()
    {
        if (UsesModelFile)
        {
            SetLlmTestResult(() => Translate.Text(
                "接続先 URL を入力してください（空のときは、アプリがモデルファイルで llama-server を起動します）。",
                "Enter the server URL (when it is empty, the app starts llama-server with the model file)."));
            return;
        }

        SetLlmTestResult(() => Translate.Text("接続しています…", "Connecting…"));
        try
        {
            var result = await _llmTester.TestAsync(EndpointText, CancellationToken.None);
            SetLlmTestResult(() => result);
        }
        catch (LlmStartException ex)
        {
            SetLlmTestResult(() => ex.Message);
        }
    }

    private void AddAccount(string prefix, string host)
    {
        var number = 1;
        while (Accounts.Any(a => a.Id == $"{prefix}{number}"))
        {
            number++;
        }

        var account = new AccountEditViewModel(
            new AccountConfig { Id = $"{prefix}{number}", Host = host, Port = 993, User = "" }, isNew: true, _credentials);
        Accounts.Add(account);
        SelectedAccount = account;
    }

    private void RemoveAccount()
    {
        if (SelectedAccount is { } account && _dialogs.Confirm(
                Translate.Text(
                    $"アカウント「{account.Id}」を削除しますか？\nこのアカウントの振り分け先の設定も消えます。保存後、再起動すると反映されます。",
                    $"Delete the account \"{account.Id}\"?\nIts destination folder settings are deleted too. This takes effect after you save and restart.")))
        {
            Accounts.Remove(account);
            SelectedAccount = Accounts.FirstOrDefault();
        }
    }

    private void RegisterPassword()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        var key = account.ToConfig().EffectiveCredentialKey;
        if (_dialogs.AskPassword(account.Id, account.User) is { Length: > 0 } password)
        {
            _credentials.WriteSecret(key, account.User, password);
            account.RefreshPasswordStatus();
            account.TestResult = Translate.Text($"パスワードを資格情報マネージャー（{key}）に登録しました。", $"Registered the password in Credential Manager ({key}).");
        }
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        account.TestResult = Translate.Text("接続しています…", "Connecting…");
        try
        {
            account.TestResult = await _tester.TestAsync(account.ToConfig(), CancellationToken.None);
        }
        catch (MailServerException ex)
        {
            account.TestResult = ex.Message;
        }
    }
}
