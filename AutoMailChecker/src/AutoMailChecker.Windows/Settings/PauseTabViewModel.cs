using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Windows.Pause;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.Settings;

public enum RuleMethod
{
    /// <summary>実行ファイル名</summary>
    Exe,
    /// <summary>ウィンドウタイトル</summary>
    Title,
    /// <summary>両方（両方に一致したときだけ対象）</summary>
    Both,
}

/// <summary>停止対象のルール1つ（画面設計 SC-03）。</summary>
public sealed class PauseRuleViewModel : EditableObject
{
    private bool _enabled;
    private string _name;
    private RuleMethod _method;
    private string _exeText;
    private string _titleText;
    private PauseAction _action;

    public PauseRuleViewModel(PauseAppRule rule)
    {
        _enabled = rule.Enabled;
        _name = rule.Name;
        _exeText = string.Join(", ", rule.Exe);
        _titleText = rule.TitleContains ?? "";
        _method = rule.Exe.Count > 0 && !string.IsNullOrWhiteSpace(rule.TitleContains) ? RuleMethod.Both
            : rule.Exe.Count > 0 ? RuleMethod.Exe
            : RuleMethod.Title;
        _action = rule.Mode;
    }

    public static IReadOnlyList<Choice<RuleMethod>> MethodChoices { get; } =
    [
        new("実行ファイル名", RuleMethod.Exe),
        new("ウィンドウタイトル", RuleMethod.Title),
        new("両方（両方に一致したときだけ）", RuleMethod.Both),
    ];

    public static IReadOnlyList<Choice<PauseAction>> ActionChoices { get; } =
    [
        new("完全停止", PauseAction.Stop),
        new("低負荷で継続", PauseAction.LowLoad),
    ];

    public bool Enabled { get => _enabled; set { if (SetProperty(ref _enabled, value)) OnPropertyChanged(nameof(Summary)); } }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Summary)); } }

    public RuleMethod Method
    {
        get => _method;
        set
        {
            if (SetProperty(ref _method, value))
            {
                OnPropertyChanged(nameof(UsesExe));
                OnPropertyChanged(nameof(UsesTitle));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    /// <summary>実行ファイル名（完全一致、大文字小文字は区別しない）。複数あれば「,」で区切る。</summary>
    public string ExeText { get => _exeText; set { if (SetProperty(ref _exeText, value)) OnPropertyChanged(nameof(Summary)); } }

    /// <summary>ウィンドウタイトル（部分一致）。</summary>
    public string TitleText { get => _titleText; set { if (SetProperty(ref _titleText, value)) OnPropertyChanged(nameof(Summary)); } }

    public PauseAction Action { get => _action; set { if (SetProperty(ref _action, value)) OnPropertyChanged(nameof(Summary)); } }

    public bool UsesExe => Method is RuleMethod.Exe or RuleMethod.Both;
    public bool UsesTitle => Method is RuleMethod.Title or RuleMethod.Both;

    /// <summary>一覧の表示（例:「Minecraft（Java版）: javaw.exe ＋ タイトル「Minecraft」→ 完全停止」）。</summary>
    public string Summary
    {
        get
        {
            var target = Method switch
            {
                RuleMethod.Exe => ExeText,
                RuleMethod.Title => $"タイトル「{TitleText}」",
                _ => $"{ExeText} ＋ タイトル「{TitleText}」",
            };
            var action = Action == PauseAction.Stop ? "完全停止" : "低負荷で継続";
            return $"{(Enabled ? "" : "（無効）")}{Name}: {target} → {action}";
        }
    }

    public PauseAppRule ToRule() => new()
    {
        Name = Name.Trim(),
        Exe = UsesExe ? ExeText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : [],
        TitleContains = UsesTitle && !string.IsNullOrWhiteSpace(TitleText) ? TitleText.Trim() : null,
        Mode = Action,
        Enabled = Enabled,
    };
}

/// <summary>固定ルール（Steam・Epic・DMM）。表示だけで、削除・無効化・動作の変更はできない。</summary>
public sealed record FixedRuleRow(string Name, string Target);

/// <summary>
/// 設定ウィンドウの「停止対象」タブ（画面設計 SC-03）。上に現在の検知、その下に固定ルールと指定ルールの一覧。
/// </summary>
public sealed class PauseTabViewModel : EditableObject
{
    private readonly IRunModeController _runModes;
    private readonly ISystemSnapshotSource _system;
    private readonly ISettingsDialogs _dialogs;
    private PauseRuleViewModel? _selectedRule;
    private string _dmmGameFolder;
    private bool _fullscreenDetect;
    private string _resumeDelayText;
    private string _currentDetection = "";

    public PauseTabViewModel(PauseConfig pause, IRunModeController runModes, ISystemSnapshotSource system, ISettingsDialogs dialogs)
    {
        _runModes = runModes;
        _system = system;
        _dialogs = dialogs;
        _dmmGameFolder = pause.DmmGameFolder ?? "";
        _fullscreenDetect = pause.FullscreenDetect;
        _resumeDelayText = pause.ResumeDelaySeconds.ToString(CultureInfo.InvariantCulture);
        Rules = new ObservableCollection<PauseRuleViewModel>(pause.Apps.Select(r => new PauseRuleViewModel(r)));
        SelectedRule = Rules.FirstOrDefault();

        AddRunningCommand = new RelayCommand(AddRunning);
        AddExeCommand = new RelayCommand(AddExe);
        AddManualCommand = new RelayCommand(() => AddRule(new PauseAppRule { Name = "新しい停止対象", Exe = ["example.exe"] }));
        DeleteCommand = new RelayCommand(Delete, () => SelectedRule is not null);
        BrowseDmmCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFolder(DmmGameFolder) is { } folder)
            {
                DmmGameFolder = folder;
            }
        });
        RefreshDetection();
    }

    public IReadOnlyList<FixedRuleRow> FixedRules { get; } =
    [
        new("Steamのゲーム", @"Steam ライブラリの steamapps\common の配下（libraryfolders.vdf から読む）"),
        new("Epic Gamesのゲーム", "Epic のゲームのインストール先の配下（マニフェストから読む。ランチャー本体は除く）"),
        new("DMM GAME PLAYERのゲーム", "下で指定したインストール先フォルダーの配下（ランチャー本体は除く）"),
    ];

    public ObservableCollection<PauseRuleViewModel> Rules { get; }

    public ICommand AddRunningCommand { get; }
    public ICommand AddExeCommand { get; }
    public ICommand AddManualCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand BrowseDmmCommand { get; }

    public PauseRuleViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetProperty(ref _selectedRule, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedRule is not null;

    public string DmmGameFolder { get => _dmmGameFolder; set => SetProperty(ref _dmmGameFolder, value); }
    public bool FullscreenDetect { get => _fullscreenDetect; set => SetProperty(ref _fullscreenDetect, value); }

    /// <summary>再開までの待ち時間（秒、0〜600、既定 60）。</summary>
    public string ResumeDelayText { get => _resumeDelayText; set => SetProperty(ref _resumeDelayText, value); }

    /// <summary>例:「現在の検知: なし」「現在の検知: Steamのゲームを検知（xxx.exe）」</summary>
    public string CurrentDetection { get => _currentDetection; private set => SetProperty(ref _currentDetection, value); }

    /// <summary>画面を開いている間、定期的に呼んで現在の検知を更新する。</summary>
    public void RefreshDetection()
    {
        var detections = _runModes.CurrentDetections;
        CurrentDetection = "現在の検知: " + (detections.Count == 0 ? "なし" : string.Join("、", detections.Select(d => d.Description)));
    }

    public IReadOnlyList<PauseAppRule> BuildRules() => Rules.Select(r => r.ToRule()).ToList();

    public new void ClearErrors()
    {
        base.ClearErrors();
        foreach (var rule in Rules)
        {
            rule.ClearErrors();
        }
    }

    private void AddRunning()
    {
        // 名前と場所の一覧から選ぶ（場所が分からないシステムのプロセスは出さない）
        var processes = _system.GetProcesses()
            .Where(p => p.Path is not null)
            .DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.ExeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_dialogs.PickRunningApp(processes) is { } picked)
        {
            AddRule(new PauseAppRule { Name = DisplayName(picked.Path!, picked.ExeName), Exe = [picked.ExeName] });
        }
    }

    private void AddExe()
    {
        if (_dialogs.PickExecutable() is { } path)
        {
            AddRule(new PauseAppRule { Name = DisplayName(path, Path.GetFileName(path)), Exe = [Path.GetFileName(path)] });
        }
    }

    private void AddRule(PauseAppRule rule)
    {
        var viewModel = new PauseRuleViewModel(rule);
        Rules.Add(viewModel);
        SelectedRule = viewModel;
    }

    private void Delete()
    {
        if (SelectedRule is { } rule && _dialogs.Confirm($"停止対象「{rule.Name}」を削除しますか？"))
        {
            var index = Rules.IndexOf(rule);
            Rules.Remove(rule);
            SelectedRule = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
        }
    }

    /// <summary>ファイルの説明（例:「Visual Studio Code」）があればそれを、なければファイル名を名前にする。</summary>
    private static string DisplayName(string path, string fallback)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
        }

        return Path.GetFileNameWithoutExtension(fallback);
    }
}
