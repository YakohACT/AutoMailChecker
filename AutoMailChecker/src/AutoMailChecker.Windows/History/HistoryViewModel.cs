using System.Collections.ObjectModel;
using System.Windows.Input;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.History;

/// <summary>お試しの回の絞り込み。</summary>
public sealed record TrialFilter(string Label, long? TrialRunId, bool NewMailOnly)
{
    public override string ToString() => Label;
}

/// <summary>
/// 判定履歴ウィンドウ（画面設計 SC-05）。上に絞り込みと集計、中央に一覧（新しい順、200 件ずつ）、下に選んだ1件の詳細と操作。
/// 「受信トレイに戻す」は確認なしで実行し、結果を詳細欄に出す。
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    public const int PageSize = 200;

    private readonly IStateStore _store;
    private readonly IConfigProvider _config;
    private readonly IUndoService _undo;
    private DateTime? _from;
    private DateTime? _to;
    private Choice<string?> _selectedAccount;
    private Choice<HistoryCategory> _selectedCategory;
    private TrialFilter _selectedTrial;
    private HistoryRow? _selectedRow;
    private Choice<string?>? _selectedCorrection;
    private string _summaryText = "";
    private string? _detailMessage;
    private bool _hasMore;
    private bool _isLoading;
    private int _loadVersion;

    public HistoryViewModel(IStateStore store, IConfigProvider config, IUndoService undo)
    {
        _store = store;
        _config = config;
        _undo = undo;

        var settings = config.Current;
        AccountChoices = [new("すべて", null), .. settings.Accounts.Select(a => new Choice<string?>(a.Id, a.Id))];
        CategoryChoices =
        [
            new("すべて", HistoryCategory.All),
            new("移動済み", HistoryCategory.Moved),
            new("移動予定", HistoryCategory.Planned),
            new("受信トレイに残した", HistoryCategory.Kept),
            new("失敗", HistoryCategory.Failed),
            new("戻した", HistoryCategory.Undone),
        ];
        TrialChoices = [new("すべて", null, false), new("新着のみ", null, true)];
        CorrectionChoices =
        [
            .. settings.Items.OrderBy(i => i.Priority).Select(i => new Choice<string?>(i.Label, i.Id.Value)),
            new("どの項目でもない", null),
        ];

        _selectedAccount = AccountChoices[0];
        _selectedCategory = CategoryChoices[0];
        _selectedTrial = TrialChoices[0];

        RefreshCommand = new AsyncCommand(ReloadAsync, onError: ShowError);
        UndoCommand = new AsyncCommand(UndoAsync, () => SelectedRow?.CanUndo == true, ShowError);
        RecordCorrectionCommand = new AsyncCommand(RecordCorrectionAsync,
            () => SelectedRow is { Record.Outcome: not Outcome.Recorded } && SelectedCorrection is not null, ShowError);
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public IReadOnlyList<Choice<string?>> AccountChoices { get; }
    public IReadOnlyList<Choice<HistoryCategory>> CategoryChoices { get; }
    public ObservableCollection<TrialFilter> TrialChoices { get; }
    public IReadOnlyList<Choice<string?>> CorrectionChoices { get; }

    public ICommand RefreshCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RecordCorrectionCommand { get; }

    public DateTime? From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime? To { get => _to; set => SetProperty(ref _to, value); }
    public Choice<string?> SelectedAccount { get => _selectedAccount; set => SetProperty(ref _selectedAccount, value); }
    public Choice<HistoryCategory> SelectedCategory { get => _selectedCategory; set => SetProperty(ref _selectedCategory, value); }
    public TrialFilter SelectedTrial { get => _selectedTrial; set => SetProperty(ref _selectedTrial, value); }
    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }

    public HistoryRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                DetailMessage = null;
                SelectedCorrection = null;
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedRow is not null;

    public Choice<string?>? SelectedCorrection
    {
        get => _selectedCorrection;
        set => SetProperty(ref _selectedCorrection, value);
    }

    public string? DetailMessage { get => _detailMessage; private set => SetProperty(ref _detailMessage, value); }

    /// <summary>画面を開いたとき。お試しの回を指定されたら、その回だけを表示する。</summary>
    public async Task InitializeAsync(long? trialRunId = null)
    {
        foreach (var run in await _store.TrialRuns.ListAsync(CancellationToken.None))
        {
            TrialChoices.Add(new TrialFilter(
                $"#{run.Id} {run.PeriodFrom:M/d}〜{run.PeriodTo:M/d}（{(run.Mode == TrialMode.Move ? "移動" : "表示のみ")}）", run.Id, false));
        }

        await ShowTrialAsync(trialRunId);
    }

    /// <summary>お試しの回で絞り込んで表示し直す（null ならすべて）。</summary>
    public async Task ShowTrialAsync(long? trialRunId)
    {
        SelectedTrial = TrialChoices.FirstOrDefault(t => t.TrialRunId == trialRunId && trialRunId is not null) ?? TrialChoices[0];
        await ReloadAsync();
    }

    /// <summary>一覧の末尾までスクロールしたら続きを読む。</summary>
    public async Task LoadMoreAsync()
    {
        if (!HasMore || _isLoading)
        {
            return;
        }

        await LoadPageAsync(_loadVersion);
    }

    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        Rows.Clear();
        SelectedRow = null;
        HasMore = false;

        var summary = await _store.Judgements.SummarizeAsync(BuildQuery(0), CancellationToken.None);
        if (version != _loadVersion)
        {
            return;
        }

        SummaryText = $"表示中 {summary.Total} 件　・　移動 {summary.Moved}　・　受信トレイに残した {summary.Kept}　・　確信度「低」 {summary.LowConfidence}";
        await LoadPageAsync(version);
    }

    private async Task LoadPageAsync(int version)
    {
        _isLoading = true;
        try
        {
            var page = await _store.Judgements.QueryAsync(BuildQuery(Rows.Count), CancellationToken.None);
            if (version != _loadVersion)
            {
                return; // 読み込み中に絞り込みが変わった
            }

            var items = _config.Current.Items;
            foreach (var record in page)
            {
                Rows.Add(new HistoryRow(record, items));
            }

            HasMore = page.Count == PageSize;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private JudgementQuery BuildQuery(int offset) => new()
    {
        ReceivedFrom = From is { } from ? new DateTimeOffset(from.Date) : null,
        ReceivedTo = To is { } to ? new DateTimeOffset(to.Date.AddDays(1)) : null,
        AccountId = SelectedAccount.Value,
        Category = SelectedCategory.Value,
        TrialRunId = SelectedTrial.TrialRunId,
        NewMailOnly = SelectedTrial.NewMailOnly,
        Offset = offset,
        Limit = PageSize,
    };

    private async Task UndoAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        DetailMessage = "受信トレイに戻しています…";
        var outcome = await _undo.UndoAsync(row.Id, asMisjudgement: true, CancellationToken.None);
        await ReplaceRowAsync(row);
        DetailMessage = outcome.Message;
    }

    private async Task RecordCorrectionAsync()
    {
        if (SelectedRow is not { } row || SelectedCorrection is not { } correction)
        {
            return;
        }

        await _store.Judgements.AddCorrectionAsync(row.Id, correction.Value, CorrectionSource.Manual, CancellationToken.None);
        await ReplaceRowAsync(row);
        DetailMessage = $"誤判定として記録しました（本来は「{correction.Label}」）。";
    }

    /// <summary>操作のあと、その行を読み直して表示を更新する。</summary>
    private async Task ReplaceRowAsync(HistoryRow row)
    {
        var updated = await _store.Judgements.GetAsync(row.Id, CancellationToken.None);
        var index = Rows.IndexOf(row);
        if (updated is null || index < 0)
        {
            return;
        }

        var message = DetailMessage;
        var replacement = new HistoryRow(updated, _config.Current.Items);
        Rows[index] = replacement;
        SelectedRow = replacement;
        DetailMessage = message;
    }

    private void ShowError(Exception ex) => DetailMessage = $"エラー: {ex.Message}";
}
