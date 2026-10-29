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
    private RejudgeResult? _result;
    private Func<string>? _message;

    public RejudgeViewModel(IStateStore store, IRejudgeService rejudge, Func<AppConfig> draft)
    {
        _store = store;
        _rejudge = rejudge;
        _draft = draft;
        RunCommand = new AsyncCommand(RunAsync, () => Selected is not null && IsAvailable,
            ex => SetMessage(() => Translate.Text($"判定できませんでした: {ex.Message}", $"Could not judge: {ex.Message}")));
    }

    public ObservableCollection<HistoryRow> Recent { get; } = [];
    public ICommand RunCommand { get; }

    public bool IsAvailable => _rejudge.IsAvailable;

    public string AvailabilityText => IsAvailable
        ? Translate.Text("保存前の設定で判定し直し、結果だけを表示します（移動はしません）。",
            "The mail is judged again with the unsaved settings and only the result is shown (no mail is moved).")
        : Translate.Text("完全停止中（llama-server が止まっている間）は試し判定できません。",
            "Test judgment is not available during a full stop (while the AI is not in use).");

    public HistoryRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    /// <summary>判定の結果（言語を切り替えたら読み直す）。</summary>
    public string? Result => _result is { } result ? Describe(result) : null;

    public string? Message => _message?.Invoke();

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

        SetResult(null);
        SetMessage(() => Translate.Text("判定しています…（1通あたり数十秒かかることがあります）", "Judging… (this can take tens of seconds per mail)"));
        var result = await _rejudge.RejudgeAsync(row.Id, _draft(), CancellationToken.None);
        SetMessage(null);
        SetResult(result);
    }

    private static string Describe(RejudgeResult result)
    {
        var judgement = result.Judgement;
        var matched = result.MatchedLabels.Count == 0
            ? Translate.Text("なし", "none")
            : string.Join(Translate.Text("、", ", "), result.MatchedLabels);
        var confidence = judgement.Confidence switch
        {
            Confidence.High => Translate.Text("高", "High"),
            Confidence.Medium => Translate.Text("中", "Medium"),
            _ => Translate.Text("低", "Low"),
        };
        return string.Join(Environment.NewLine,
            Translate.Text($"該当項目: {matched}", $"Matched items: {matched}"),
            Translate.Text($"確信度: {confidence}", $"Confidence: {confidence}"),
            Translate.Text($"振り分け先: {result.Destination}", $"Destination: {result.Destination}"),
            Translate.Text($"要約: {judgement.Summary}", $"Summary: {judgement.Summary}"),
            Translate.Text($"判定理由: {judgement.Reason}", $"Reason: {judgement.Reason}"),
            Translate.Text($"（判定にかかった時間 {result.Duration.TotalSeconds:0.0} 秒）", $"(judged in {result.Duration.TotalSeconds:0.0} s)"));
    }

    private void SetResult(RejudgeResult? result)
    {
        _result = result;
        OnPropertyChanged(nameof(Result));
    }

    private void SetMessage(Func<string>? message)
    {
        _message = message;
        OnPropertyChanged(nameof(Message));
    }
}
