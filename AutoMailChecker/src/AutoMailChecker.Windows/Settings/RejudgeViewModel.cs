using System.Collections.ObjectModel;
using System.Windows.Input;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.History;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.Settings;

/// <summary>
/// 試し判定ダイアログ。判定履歴から最近のメールを1通選び、保存前の設定で判定し直して、該当項目・確信度・理由を出す。移動はしない。
/// 完全停止中（llama-server 終了中）は押せない。
/// </summary>
public sealed class RejudgeViewModel : ObservableObject
{
    public const int RecentCount = 50;

    private readonly IStateStore _store;
    private readonly IRejudgeService _rejudge;
    private readonly Func<AppConfig> _draft;
    private HistoryRow? _selected;
    private string? _result;
    private string? _message;

    public RejudgeViewModel(IStateStore store, IRejudgeService rejudge, Func<AppConfig> draft)
    {
        _store = store;
        _rejudge = rejudge;
        _draft = draft;
        RunCommand = new AsyncCommand(RunAsync, () => Selected is not null && IsAvailable, ex => Message = $"判定できませんでした: {ex.Message}");
    }

    public ObservableCollection<HistoryRow> Recent { get; } = [];
    public ICommand RunCommand { get; }

    public bool IsAvailable => _rejudge.IsAvailable;

    public string AvailabilityText => IsAvailable
        ? "保存前の設定で判定し直し、結果だけを表示します（移動はしません）。"
        : "完全停止中（llama-server が止まっている間）は試し判定できません。";

    public HistoryRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public string? Result { get => _result; private set => SetProperty(ref _result, value); }
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public async Task InitializeAsync()
    {
        var items = _draft().Items;
        foreach (var record in await _store.Judgements.QueryAsync(new JudgementQuery { Limit = RecentCount }, CancellationToken.None))
        {
            Recent.Add(new HistoryRow(record, items));
        }

        Selected = Recent.FirstOrDefault();
    }

    private async Task RunAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        Result = null;
        Message = "判定しています…（1通あたり数十秒かかることがあります）";
        var result = await _rejudge.RejudgeAsync(row.Id, _draft(), CancellationToken.None);
        var judgement = result.Judgement;
        Message = null;
        Result = string.Join(Environment.NewLine,
            $"該当項目: {(result.MatchedLabels.Count == 0 ? "なし" : string.Join("、", result.MatchedLabels))}",
            $"広告: {(judgement.IsAdvertisement ? "はい" : "いいえ")}",
            $"確信度: {judgement.Confidence switch { Confidence.High => "高", Confidence.Medium => "中", _ => "低" }}",
            $"振り分け先: {result.Destination}",
            $"要約: {judgement.Summary}",
            $"判定理由: {judgement.Reason}",
            $"（判定にかかった時間 {result.Duration.TotalSeconds:0.0} 秒）");
    }
}
