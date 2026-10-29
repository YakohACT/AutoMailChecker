using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows.Input;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.Settings;

/// <summary>1アカウント分の振り分け先。候補はそのアカウントのフォルダーだけで、ほかの項目で使用中のものは出さない。</summary>
public sealed class FolderChoice(string accountId, string? folder) : EditableObject
{
    private string _folder = folder ?? "";

    public event EventHandler? FolderChanged;

    public string AccountId { get; } = accountId;

    /// <summary>空なら未設定（このアカウントでは移動しない）。</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            if (SetProperty(ref _folder, value ?? ""))
            {
                FolderChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public ObservableCollection<string> Suggestions { get; } = [];

    public string? Value => string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim();

    internal void SetSuggestions(IReadOnlyList<string> folders)
    {
        if (Suggestions.SequenceEqual(folders))
        {
            return;
        }

        // 選んでいるフォルダーは候補から消さない（消すと選択が外れるため）
        foreach (var stale in Suggestions.Where(s => !folders.Contains(s) && s != Folder).ToList())
        {
            Suggestions.Remove(stale);
        }

        for (var i = 0; i < folders.Count; i++)
        {
            if (!Suggestions.Contains(folders[i]))
            {
                Suggestions.Insert(Math.Min(i, Suggestions.Count), folders[i]);
            }
        }
    }
}

/// <summary>項目1つの編集欄（画面設計 SC-02）。</summary>
public sealed class ItemEditViewModel : EditableObject
{
    private string _label;
    private string _description;
    private string _examplesText;
    private string _counterExamplesText;
    private bool _enabled;
    private int _priority;

    public ItemEditViewModel(ItemDefinition definition, IEnumerable<string> accountIds)
    {
        Id = definition.Id;
        _label = definition.Label;
        _description = definition.Description;
        _examplesText = string.Join(Environment.NewLine, definition.Examples);
        _counterExamplesText = string.Join(Environment.NewLine, definition.CounterExamples);
        _enabled = definition.Enabled;
        _priority = definition.Priority;
        Folders = new ObservableCollection<FolderChoice>(accountIds.Select(a => new FolderChoice(a, definition.FolderFor(a))));
    }

    /// <summary>追加時に自動で振り、以後は変えない。画面には出さない。</summary>
    public ItemId Id { get; }

    public string Label { get => _label; set { if (SetProperty(ref _label, value)) OnPropertyChanged(nameof(ListText)); } }
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    /// <summary>1行に1例（0〜3行、1行50文字以内）。</summary>
    public string ExamplesText { get => _examplesText; set => SetProperty(ref _examplesText, value); }

    public string CounterExamplesText { get => _counterExamplesText; set => SetProperty(ref _counterExamplesText, value); }
    public bool Enabled { get => _enabled; set { if (SetProperty(ref _enabled, value)) OnPropertyChanged(nameof(ListText)); } }

    public int Priority
    {
        get => _priority;
        internal set { if (SetProperty(ref _priority, value)) OnPropertyChanged(nameof(ListText)); }
    }

    /// <summary>一覧の表示（例:「1. 重要」）。並び順がそのまま優先度になる。</summary>
    public string ListText => $"{Priority}. {Label}" + (Enabled ? "" : Translate.Text("（無効）", " (off)"));

    public ObservableCollection<FolderChoice> Folders { get; }

    public ItemDefinition ToDefinition() => new()
    {
        Id = Id,
        Label = Label.Trim(),
        Priority = Priority,
        Description = Description.Trim(),
        Examples = Lines(ExamplesText),
        CounterExamples = Lines(CounterExamplesText),
        Folders = Folders.Where(f => f.Value is not null).ToDictionary(f => f.AccountId, f => f.Value!),
        Enabled = Enabled,
    };

    internal static List<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>
/// 設定ウィンドウの「項目」タブ（画面設計 SC-02）。左に項目一覧（並び順が優先度）、右に選んだ項目の編集欄。
/// 振り分け先の候補は画面を開いたときに各アカウントへ問い合わせ、10 分間使い回す。
/// </summary>
public sealed class ItemsTabViewModel : ObservableObject
{
    private readonly IMailFolderCatalog _catalog;
    private readonly IRejudgeService _rejudge;
    private readonly IStateStore _store;
    private readonly ISettingsDialogs _dialogs;
    private readonly Func<AppConfig> _draft;
    private readonly Dictionary<string, IReadOnlyList<string>> _serverFolders = [];
    private List<string> _accountIds;
    private ItemEditViewModel? _selectedItem;
    private Func<string> _folderStatus = () => "";

    public ItemsTabViewModel(
        AppConfig config, IMailFolderCatalog catalog, IRejudgeService rejudge, IStateStore store,
        ISettingsDialogs dialogs, Func<AppConfig> draft)
    {
        _catalog = catalog;
        _rejudge = rejudge;
        _store = store;
        _dialogs = dialogs;
        _draft = draft;
        _accountIds = config.Accounts.Select(a => a.Id).ToList();

        foreach (var item in config.Items.OrderBy(i => i.Priority))
        {
            AddItem(new ItemEditViewModel(item, _accountIds));
        }

        Renumber();
        SelectedItem = Items.FirstOrDefault();

        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(Delete, () => SelectedItem is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => SelectedItem is { } s && Items.IndexOf(s) > 0);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => SelectedItem is { } s && Items.IndexOf(s) < Items.Count - 1);
        RefreshFoldersCommand = new AsyncCommand(() => LoadFoldersAsync(refresh: true));
        RejudgeCommand = new RelayCommand(OpenRejudge);
    }

    public ObservableCollection<ItemEditViewModel> Items { get; } = [];

    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RefreshFoldersCommand { get; }
    public ICommand RejudgeCommand { get; }

    public ItemEditViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>フォルダー一覧の取得状況（例:「icloud: 取得できません（パスワードが登録されていません）」）。</summary>
    public string FolderStatus => _folderStatus();

    /// <summary>各アカウントのフォルダー一覧を問い合わせ、振り分け先の候補を作る。</summary>
    public async Task LoadFoldersAsync(bool refresh)
    {
        SetFolderStatus(() => Translate.Text("フォルダー一覧を取得しています…", "Loading the folder lists…"));
        var problems = new List<string>();
        foreach (var accountId in _accountIds.ToList())
        {
            try
            {
                _serverFolders[accountId] = await _catalog.GetSortingFoldersAsync(accountId, refresh, CancellationToken.None);
            }
            catch (Exception ex) when (ex is MailServerException or ArgumentException or OperationCanceledException)
            {
                _serverFolders.Remove(accountId);
                problems.Add($"{accountId}: {ex.Message}");
            }
        }

        RecomputeSuggestions();
        SetFolderStatus(() => problems.Count == 0
            ? Translate.Text(
                "振り分け先は、各アカウントに既にあるフォルダーから選びます（アプリはフォルダーを作りません）。",
                "Choose destinations from folders that already exist in each account (the app does not create folders).")
            : Translate.Text(
                "フォルダー一覧を取得できないアカウントがあります。フォルダー名を直接入力することもできます。",
                "The folder list could not be loaded for some accounts. You can also type folder names directly.")
                + "\n" + string.Join("\n", problems));
    }

    private void SetFolderStatus(Func<string> status)
    {
        _folderStatus = status;
        OnPropertyChanged(nameof(FolderStatus));
    }

    /// <summary>全般タブでアカウントを追加・削除したら、振り分け先の欄も合わせる。</summary>
    public void SyncAccounts(IReadOnlyList<string> accountIds)
    {
        _accountIds = accountIds.ToList();
        foreach (var item in Items)
        {
            Sync(item.Folders, accountIds, accountId => new FolderChoice(accountId, null));
        }

        foreach (var choice in Items.SelectMany(i => i.Folders))
        {
            choice.FolderChanged -= OnFolderChanged;
            choice.FolderChanged += OnFolderChanged;
        }

        RecomputeSuggestions();
    }

    /// <summary>一覧のドラッグで並べ替える。上にあるほど優先。</summary>
    public void Move(ItemEditViewModel item, int newIndex)
    {
        var oldIndex = Items.IndexOf(item);
        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        Items.Move(oldIndex, newIndex);
        Renumber();
        SelectedItem = item;
    }

    public IReadOnlyList<ItemDefinition> BuildItems() => Items.Select(i => i.ToDefinition()).ToList();

    /// <summary>サーバーに見つからない振り分け先（保存はできるが、その振り分け先への移動は止まる）。</summary>
    public IEnumerable<string> FolderWarnings()
    {
        foreach (var choice in Items.SelectMany(i => i.Folders))
        {
            if (choice.Value is { } folder && _serverFolders.TryGetValue(choice.AccountId, out var known) && !known.Contains(folder))
            {
                yield return Translate.Text(
                    $"{choice.AccountId} に振り分け先フォルダー「{folder}」がありません（そのフォルダーへの移動は止まります）。",
                    $"{choice.AccountId} has no destination folder \"{folder}\" (moves to that folder are stopped).");
            }
        }
    }

    public void ClearErrors()
    {
        foreach (var item in Items)
        {
            item.ClearErrors();
            foreach (var folder in item.Folders)
            {
                folder.ClearErrors();
            }
        }
    }

    private void Add()
    {
        var item = new ItemEditViewModel(new ItemDefinition
        {
            Id = NewItemId(),
            Label = Translate.Text("新しい項目", "New item"),
            Description = "",
        }, _accountIds);
        AddItem(item);
        Renumber();
        RecomputeSuggestions();
        SelectedItem = item;
    }

    private void Delete()
    {
        if (SelectedItem is not { } item
            || !_dialogs.Confirm(Translate.Text(
                $"項目「{item.Label}」を削除しますか？\n過去の判定履歴には表示名の控えが残ります。\n（保存するまでは設定ファイルに反映されません）",
                $"Delete the item \"{item.Label}\"?\nPast judgment history keeps a copy of its display name.\n(The settings file does not change until you save.)")))
        {
            return;
        }

        var index = Items.IndexOf(item);
        Items.Remove(item);
        foreach (var folder in item.Folders)
        {
            folder.FolderChanged -= OnFolderChanged;
        }

        Renumber();
        RecomputeSuggestions();
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    private void MoveSelected(int offset)
    {
        if (SelectedItem is { } item)
        {
            Move(item, Items.IndexOf(item) + offset);
        }
    }

    private void OpenRejudge() =>
        _dialogs.ShowRejudge(new RejudgeViewModel(_store, _rejudge, _draft));

    private void AddItem(ItemEditViewModel item)
    {
        Items.Add(item);
        foreach (var folder in item.Folders)
        {
            folder.FolderChanged += OnFolderChanged;
        }
    }

    private void OnFolderChanged(object? sender, EventArgs e) => RecomputeSuggestions();

    /// <summary>候補は、そのアカウントのフォルダーから、ほかの項目で使用中のものを除いたもの。</summary>
    private void RecomputeSuggestions()
    {
        var all = Items.SelectMany(i => i.Folders).ToList();
        foreach (var choice in all)
        {
            var server = _serverFolders.GetValueOrDefault(choice.AccountId) ?? [];
            var usedByOthers = all
                .Where(c => !ReferenceEquals(c, choice) && c.AccountId == choice.AccountId && c.Value is not null)
                .Select(c => c.Value!)
                .ToHashSet(StringComparer.Ordinal);
            choice.SetSuggestions(server.Where(f => !usedByOthers.Contains(f)).ToList());
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Priority = i + 1;
        }
    }

    private ItemId NewItemId()
    {
        while (true)
        {
            var id = new ItemId($"item_{RandomNumberGenerator.GetHexString(4, lowercase: true)}");
            if (Items.All(i => i.Id != id))
            {
                return id;
            }
        }
    }

    private static void Sync(ObservableCollection<FolderChoice> choices, IReadOnlyList<string> accountIds, Func<string, FolderChoice> create)
    {
        foreach (var stale in choices.Where(c => !accountIds.Contains(c.AccountId)).ToList())
        {
            choices.Remove(stale);
        }

        foreach (var accountId in accountIds.Where(a => choices.All(c => c.AccountId != a)))
        {
            choices.Add(create(accountId));
        }
    }
}
