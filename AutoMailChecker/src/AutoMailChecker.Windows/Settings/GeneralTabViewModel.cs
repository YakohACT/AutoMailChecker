using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
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
    private string _passwordStatus = "";
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
    public string PasswordStatus { get => _passwordStatus; private set => SetProperty(ref _passwordStatus, value); }
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
            PasswordStatus = _credentials.ReadSecret(ToConfig().EffectiveCredentialKey) is { Length: > 0 } ? "登録済み" : "未登録";
        }
        catch (Win32Exception)
        {
            PasswordStatus = "確認できません";
        }
    }
}

/// <summary>
/// 設定ウィンドウの「全般」タブ（画面設計 SC-04）。アカウント、自動仕分け、確信度のしきい値、ポップアップ、履歴の保持日数、
/// モデルファイル、Windows のログオン時の自動起動。
/// </summary>
public sealed class GeneralTabViewModel : EditableObject
{
    private readonly ICredentialStore _credentials;
    private readonly IMailConnectionTester _tester;
    private readonly IAutoStartRegistrar _autoStart;
    private readonly IStateStore _store;
    private readonly ISettingsDialogs _dialogs;
    private readonly string _logDirectory;
    private AccountEditViewModel? _selectedAccount;
    private bool _autoSort;
    private bool _highConfidenceOnly;
    private bool _errorPopup;
    private string _retentionDaysText;
    private string _modelPath;
    private bool _autoStartEnabled;
    private string? _autoStartMessage;
    private bool _hasCompletedTrial;

    public GeneralTabViewModel(
        AppConfig config, ICredentialStore credentials, IMailConnectionTester tester, IAutoStartRegistrar autoStart,
        IStateStore store, ISettingsDialogs dialogs, string logDirectory)
    {
        _credentials = credentials;
        _tester = tester;
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
        _modelPath = config.Llm.Model;

        AddICloudCommand = new RelayCommand(() => AddAccount("icloud", "imap.mail.me.com"));
        AddGmailCommand = new RelayCommand(() => AddAccount("gmail", "imap.gmail.com"));
        RemoveAccountCommand = new RelayCommand(RemoveAccount, () => SelectedAccount is not null);
        RegisterPasswordCommand = new RelayCommand(RegisterPassword, () => SelectedAccount is not null);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => SelectedAccount is not null);
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
                    "まだお試し仕分けを行っていません。\n新着メールの自動仕分けを始めると、届いたメールが実際に振り分け先へ移動します。\nオンにしますか？"))
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

    /// <summary>モデルファイル。変更は再起動が必要。</summary>
    public string ModelPath { get => _modelPath; set => SetProperty(ref _modelPath, value); }

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
                    AutoStartMessage = "タスクスケジューラに登録しました。次回のログオンから自動で起動します。";
                }
                else
                {
                    _autoStart.Unregister();
                    AutoStartMessage = "タスクスケジューラから削除しました。";
                }

                _autoStartEnabled = value;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
            {
                AutoStartMessage = ex.Message;
            }

            OnPropertyChanged();
        }
    }

    public string? AutoStartMessage { get => _autoStartMessage; private set => SetProperty(ref _autoStartMessage, value); }

    /// <summary>画面を開いたとき。自動起動の登録状況と、お試し仕分けを行ったことがあるかを調べる。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _autoStartEnabled = await Task.Run(_autoStart.IsRegistered);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
        {
            AutoStartMessage = $"自動起動の登録状況を確認できません: {ex.Message}";
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
                $"アカウント「{account.Id}」を削除しますか？\nこのアカウントの振り分け先の設定も消えます。保存後、再起動すると反映されます。"))
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
            account.TestResult = $"パスワードを資格情報マネージャー（{key}）に登録しました。";
        }
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        account.TestResult = "接続しています…";
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
