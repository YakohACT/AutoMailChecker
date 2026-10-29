using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.Tray;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.Trial;

public enum TrialStage
{
    Setup,
    Running,
    Done,
}

public sealed class AccountChoice(string id) : ObservableObject
{
    private bool _isSelected = true;

    public string Id { get; } = id;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// お試し仕分けダイアログ（画面設計 SC-06）。設定 → 実行中 → 完了 の3段階で表示を切り替える。
/// ダイアログを閉じても、お試し仕分けは裏で続く。
/// </summary>
public sealed class TrialViewModel : ObservableObject, IDisposable
{
    private readonly ITrialService _trials;
    private readonly IStateStore _store;
    private readonly IAppStatus _status;
    private readonly Action<long> _openHistory;
    private readonly Func<string, bool> _confirm;
    private readonly Dispatcher _dispatcher;
    private TrialStage _stage;
    private DateTime? _from;
    private DateTime? _to;
    private string _folder = AppInfo.InboxFolder;
    private bool _moveMode;
    private string _maxMessagesText;
    private Func<string>? _estimateText;
    private Func<string>? _message;
    private long? _runId;
    private int _processed;
    private int _total;
    private AppStatusSnapshot _lastStatus = AppStatusSnapshot.Initial;
    private TrialReport? _report;

    public TrialViewModel(
        ITrialService trials, IStateStore store, IConfigProvider config, IAppStatus status,
        Action<long> openHistory, Func<string, bool> confirm)
    {
        _trials = trials;
        _store = store;
        _status = status;
        _openHistory = openHistory;
        _confirm = confirm;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var today = DateTime.Today;
        _from = today.AddDays(-30);
        _to = today;
        _maxMessagesText = config.Current.Trial.MaxMessages?.ToString(CultureInfo.InvariantCulture) ?? "";
        Accounts = new ObservableCollection<AccountChoice>(config.Current.Accounts.Select(a => new AccountChoice(a.Id)));

        EstimateCommand = new AsyncCommand(EstimateAsync, onError: ShowError);
        StartCommand = new AsyncCommand(StartAsync, onError: ShowError);
        CancelCommand = new AsyncCommand(CancelAsync, () => RunId is not null, ShowError);
        OpenHistoryCommand = new AsyncCommand(() =>
        {
            if (Report is { } report)
            {
                _openHistory(report.Run.Id);
            }

            return Task.CompletedTask;
        });
        RollbackCommand = new AsyncCommand(RollbackAsync, () => CanRollback, ShowError);
        NewTrialCommand = new AsyncCommand(() =>
        {
            Stage = TrialStage.Setup;
            SetMessage(null);
            return Task.CompletedTask;
        });

        _status.Changed += OnStatusChanged;
    }

    public ObservableCollection<AccountChoice> Accounts { get; }
    public ObservableCollection<TrialItemCount> ItemCounts { get; } = [];

    public ICommand EstimateCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand NewTrialCommand { get; }

    public TrialStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(IsSetup));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsDone));
            }
        }
    }

    public bool IsSetup => Stage == TrialStage.Setup;
    public bool IsRunning => Stage == TrialStage.Running;
    public bool IsDone => Stage == TrialStage.Done;

    public DateTime? From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime? To { get => _to; set => SetProperty(ref _to, value); }

    /// <summary>受信トレイ以外を選ぶと、実行方法は「結果の表示のみ」に固定する。</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            if (SetProperty(ref _folder, value))
            {
                OnPropertyChanged(nameof(CanMove));
                if (!CanMove)
                {
                    MoveMode = false;
                }
            }
        }
    }

    public bool CanMove => AppInfo.IsInbox(Folder.Trim());

    /// <summary>false なら「結果の表示のみ」、true なら「実際に移動する」。</summary>
    public bool MoveMode
    {
        get => _moveMode;
        set
        {
            if (SetProperty(ref _moveMode, value && CanMove))
            {
                OnPropertyChanged(nameof(PreviewMode));
            }
        }
    }

    public bool PreviewMode { get => !MoveMode; set => MoveMode = !value; }

    public string MaxMessagesText { get => _maxMessagesText; set => SetProperty(ref _maxMessagesText, value); }
    /// <summary>対象の件数と見込み時間。文言は読むたびに作る（言語を切り替えたら読み直す）。</summary>
    public string? EstimateText => _estimateText?.Invoke();

    public string? Message => _message?.Invoke();

    public long? RunId { get => _runId; private set => SetProperty(ref _runId, value); }

    public string ProgressText => Translate.Text($"{_processed} / {_total} 件", $"{_processed} / {_total}");
    public double ProgressPercent => _total == 0 ? 0 : 100.0 * _processed / _total;
    public string RunModeText =>
        Translate.Text("現在の運転モード: ", "Current mode: ") + TrayStatusFormatter.StatusLine(_lastStatus, DateTimeOffset.Now);

    public TrialReport? Report
    {
        get => _report;
        private set
        {
            if (SetProperty(ref _report, value))
            {
                OnPropertyChanged(nameof(ReportTitle));
                OnPropertyChanged(nameof(ReportCountsText));
                OnPropertyChanged(nameof(AccuracyText));
                OnPropertyChanged(nameof(CanRollback));
                CommandManager.InvalidateRequerySuggested(); // 「まとめて戻す」を押せるかを確かめ直す
            }
        }
    }

    public string ReportTitle => Report is { } r
        ? Translate.Text(
            $"お試し仕分け #{r.Run.Id}（{r.Run.PeriodFrom:yyyy/M/d}〜{r.Run.PeriodTo:yyyy/M/d}、{ModeLabel(r.Run.Mode)}）{StatusLabel(r.Run)}",
            $"Trial sort #{r.Run.Id} ({r.Run.PeriodFrom:yyyy/M/d}–{r.Run.PeriodTo:yyyy/M/d}, {ModeLabel(r.Run.Mode)}){StatusLabel(r.Run)}")
        : "";

    public string ReportCountsText => Report is { } r
        ? Translate.Text(
            $"対象 {r.Run.Total} 件　・　確信度「低」 {r.LowConfidence} 件　・　該当なし {r.NoMatch} 件　・　失敗 {r.Failed} 件",
            $"Total {r.Run.Total}  ·  Low confidence {r.LowConfidence}  ·  No match {r.NoMatch}  ·  Failed {r.Failed}")
        : "";

    public string AccuracyText => Report?.Accuracy is { } a
        ? Translate.Text(
            $"この回の精度（誤判定の記録 {a.Corrections} 件から）: 「{a.KeyItemLabel}」の見逃し率 {Percent(a.MissRate)}（{a.KeyItemTotal} 件中）"
                + $"　・　誤振り分け率 {Percent(a.MisrouteRate)}（{a.RoutedTotal} 件中）",
            $"Accuracy of this run (from {a.Corrections} misjudgment records): miss rate for \"{a.KeyItemLabel}\" {Percent(a.MissRate)} (of {a.KeyItemTotal})"
                + $"  ·  wrong destination rate {Percent(a.MisrouteRate)} (of {a.RoutedTotal})")
        : Translate.Text(
            "判定履歴で誤判定を記録すると、ここに「この回の精度」（見逃し率・誤振り分け率）が出ます。",
            "Record misjudgments in the history to see the accuracy of this run (miss rate and wrong destination rate) here.");

    /// <summary>「この回の移動をまとめて戻す」は、実際に移動した回で、まだ戻していないときだけ。</summary>
    public bool CanRollback => Report is { Run.Mode: TrialMode.Move, Run.RolledBackAt: null, Moved: > 0 };

    /// <summary>開いたとき。実行中の回があればその進み具合を、なければ設定を表示する。</summary>
    public async Task InitializeAsync()
    {
        var running = (await _store.TrialRuns.ListAsync(CancellationToken.None))
            .FirstOrDefault(r => r.Status == TrialRunStatus.Running);
        if (running is not null)
        {
            ShowRunning(running.Id, running.Processed, running.Total);
        }
        else
        {
            Stage = TrialStage.Setup;
        }

        UpdateRunMode(_status.Current);
    }

    public void Dispose() => _status.Changed -= OnStatusChanged;

    /// <summary>見込み時間の表示（例:「約3時間36分」）。</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var minutes = (int)Math.Ceiling(duration.TotalMinutes);
        return minutes < 60
            ? Translate.Text($"約{minutes}分", $"about {minutes} min")
            : Translate.Text($"約{minutes / 60}時間{minutes % 60}分", $"about {minutes / 60} h {minutes % 60} min");
    }

    private TrialRequest BuildRequest()
    {
        if (From is not { } from || To is not { } to)
        {
            throw new ArgumentException(Translate.Text("期間を指定してください。", "Specify the period."));
        }

        // 空欄なら上限なし
        int? max = null;
        if (!string.IsNullOrWhiteSpace(MaxMessagesText))
        {
            if (!int.TryParse(MaxMessagesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException(Translate.Text(
                    "件数の上限は数字で指定してください（空欄なら上限なし）。",
                    "Enter a number for the maximum (leave it empty for no limit)."));
            }

            max = value;
        }

        var accounts = Accounts.Where(a => a.IsSelected).Select(a => a.Id).ToList();
        return new TrialRequest(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to), accounts, Folder.Trim(),
            MoveMode ? TrialMode.Move : TrialMode.Preview, max);
    }

    private async Task EstimateAsync()
    {
        var request = BuildRequest();
        SetMessage(null);
        SetEstimate(() => Translate.Text("対象を数えています…", "Counting mails…"));
        var estimate = await _trials.EstimateAsync(request, CancellationToken.None);
        SetEstimate(() => estimate.Found > estimate.Selected
            ? Translate.Text(
                $"対象 約{estimate.Found}件（上限で {estimate.Selected} 件に間引き）　/　見込み {FormatDuration(estimate.Total)}",
                $"About {estimate.Found} mails ({estimate.Selected} after applying the maximum)  /  estimated {FormatDuration(estimate.Total)}")
            : Translate.Text(
                $"対象 約{estimate.Selected}件　/　見込み {FormatDuration(estimate.Total)}",
                $"About {estimate.Selected} mails  /  estimated {FormatDuration(estimate.Total)}"));
    }

    private async Task StartAsync()
    {
        var request = BuildRequest();
        if (request.Mode == TrialMode.Move && !_confirm(Translate.Text(
                "対象のメールを実際に振り分け先へ移動します。\n終わったあと、この回の移動はまとめて受信トレイへ戻せます。\n始めますか？",
                "The mails will actually be moved to their destinations.\nAfterwards, you can move all mail of this run back to the inbox at once.\nStart now?")))
        {
            return;
        }

        SetMessage(null);
        SetEstimate(() => Translate.Text("対象を集めています…", "Collecting mails…"));
        var run = await _trials.StartAsync(request, CancellationToken.None);
        SetEstimate(null);
        if (run.Status == TrialRunStatus.Running)
        {
            ShowRunning(run.Id, run.Processed, run.Total);
        }
        else
        {
            await ShowReportAsync(run.Id);
        }
    }

    private async Task CancelAsync()
    {
        if (RunId is not { } id)
        {
            return;
        }

        await _trials.CancelAsync(id, CancellationToken.None);
        await ShowReportAsync(id);
    }

    private async Task RollbackAsync()
    {
        if (Report is not { } report || !_confirm(Translate.Text(
                "この回で移動したメールを、まとめて受信トレイへ戻しますか？", "Move all mail moved in this run back to the inbox?")))
        {
            return;
        }

        SetMessage(() => Translate.Text("受信トレイへ戻しています…", "Moving mail back to the inbox…"));
        var summary = await _trials.RollbackAsync(report.Run.Id, CancellationToken.None);
        await ShowReportAsync(report.Run.Id);
        SetMessage(() => Translate.Text(
            $"受信トレイへ戻しました（戻した {summary.Restored} 件、見つからない {summary.NotFound} 件、失敗 {summary.Failed} 件）。",
            $"Moved back to the inbox (moved back {summary.Restored}, not found {summary.NotFound}, failed {summary.Failed})."));
    }

    private void ShowRunning(long runId, int processed, int total)
    {
        RunId = runId;
        SetProgress(processed, total);
        Stage = TrialStage.Running;
    }

    internal async Task ShowReportAsync(long runId)
    {
        var report = await _trials.GetReportAsync(runId, CancellationToken.None);
        ItemCounts.Clear();
        foreach (var count in report.ItemCounts)
        {
            ItemCounts.Add(count);
        }

        Report = report;
        RunId = null;
        Stage = TrialStage.Done;
    }

    private void OnStatusChanged(object? sender, AppStatusSnapshot status) =>
        _dispatcher.InvokeAsync(async () =>
        {
            UpdateRunMode(status);
            if (Stage != TrialStage.Running || RunId is not { } runId)
            {
                return;
            }

            if (status.Trial is { } trial && trial.TrialRunId == runId)
            {
                SetProgress(trial.Processed, trial.Total);
            }
            else if (status.Trial is null)
            {
                try
                {
                    await ShowReportAsync(runId); // 終わった
                }
                catch (Exception ex)
                {
                    ShowError(ex);
                }
            }
        });

    private void SetProgress(int processed, int total)
    {
        _processed = processed;
        _total = total;
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    private void UpdateRunMode(AppStatusSnapshot status)
    {
        _lastStatus = status;
        OnPropertyChanged(nameof(RunModeText));
    }

    private void ShowError(Exception ex)
    {
        SetEstimate(null);
        SetMessage(() => ex is ArgumentException ? ex.Message : Translate.Text($"エラー: {ex.Message}", $"Error: {ex.Message}"));
    }

    private void SetEstimate(Func<string>? text)
    {
        _estimateText = text;
        OnPropertyChanged(nameof(EstimateText));
    }

    private void SetMessage(Func<string>? message)
    {
        _message = message;
        OnPropertyChanged(nameof(Message));
    }

    private static string ModeLabel(TrialMode mode) =>
        mode == TrialMode.Move ? Translate.Text("実際に移動", "moved") : Translate.Text("結果の表示のみ", "results only");

    private static string StatusLabel(TrialRun run) => run switch
    {
        { RolledBackAt: not null } => Translate.Text("　まとめて戻し済み", "  (all moves undone)"),
        { Status: TrialRunStatus.Cancelled } => Translate.Text("　中止", "  (stopped)"),
        _ => "",
    };

    private static string Percent(double? rate) => rate is { } r ? $"{r:P0}" : "—";
}
