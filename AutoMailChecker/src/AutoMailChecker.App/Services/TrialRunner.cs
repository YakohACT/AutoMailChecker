using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// お試し仕分け（仕様書9章、シーケンス SQ-4）。期間を指定した過去のメールを集めて処理待ちに積み、
/// 判定と振り分けは新着と同じ MailProcessingService が行う（新着が常に先）。起点 UID は変えない。
/// </summary>
public sealed class TrialRunner(
    IStateStore store, IMailAccountRegistry accounts, IUndoService undo, IConfigProvider config,
    QueueSignal queueSignal, AppStatusService status, ILogger<TrialRunner> logger) : ITrialService
{
    /// <summary>実測の平均が溜まるまでの、1通あたりの見込み時間（仕様書の目標値）。</summary>
    public static readonly TimeSpan DefaultPerMail = TimeSpan.FromSeconds(108);

    private const int DurationSampleSize = 50;
    private const int MinDurationSamples = 10;
    private const int PageSize = 500;

    public async Task<TrialEstimate> EstimateAsync(TrialRequest request, CancellationToken ct)
    {
        Validate(request);
        var found = await SearchAsync(request, ct).ConfigureAwait(false);
        var selected = request.MaxMessages is { } max ? Math.Min(found.Count, max) : found.Count;
        var perMail = await store.Judgements.AverageDurationAsync(DurationSampleSize, MinDurationSamples, ct).ConfigureAwait(false)
            ?? DefaultPerMail;
        return new TrialEstimate(found.Count, selected, perMail, perMail * selected);
    }

    public async Task<TrialRun> StartAsync(TrialRequest request, CancellationToken ct)
    {
        Validate(request);
        var found = await SearchAsync(request, ct).ConfigureAwait(false);
        var targets = TrialSampler.Select(found, request.MaxMessages);

        // 受信トレイ以外のフォルダーは「結果の表示のみ」に固定する
        var mode = AppInfo.IsInbox(request.Folder) ? request.Mode : TrialMode.Preview;
        var run = await store.TrialRuns.CreateAsync(new TrialRun
        {
            PeriodFrom = request.From,
            PeriodTo = request.To,
            Accounts = request.Accounts,
            Folders = [request.Folder],
            Mode = mode,
            MaxMessages = request.MaxMessages,
        }, targets.Select(t => t.Ref).ToList(), ct).ConfigureAwait(false);

        logger.LogInformation("お試し仕分け {Id} を始めました（{Mode}、{From:yyyy-MM-dd}〜{To:yyyy-MM-dd}、{Total}/{Found} 件）。",
            run.Id, mode == TrialMode.Move ? "実際に移動する" : "結果の表示のみ", request.From, request.To, run.Total, found.Count);

        if (run.Total == 0)
        {
            await store.TrialRuns.FinishAsync(run.Id, TrialRunStatus.Done, ct).ConfigureAwait(false);
            return await store.TrialRuns.GetAsync(run.Id, ct).ConfigureAwait(false) ?? run;
        }

        status.SetTrial(new TrialProgress(run.Id, 0, run.Total));
        queueSignal.Notify();
        return run;
    }

    public async Task CancelAsync(long trialRunId, CancellationToken ct)
    {
        await store.TrialRuns.FinishAsync(trialRunId, TrialRunStatus.Cancelled, ct).ConfigureAwait(false);
        if (status.Current.Trial?.TrialRunId == trialRunId)
        {
            status.SetTrial(null);
        }

        logger.LogInformation("お試し仕分け {Id} を中止しました。", trialRunId);
    }

    public async Task<RollbackSummary> RollbackAsync(long trialRunId, CancellationToken ct)
    {
        int restored = 0, notFound = 0, failed = 0;
        foreach (var judgement in await LoadJudgementsAsync(trialRunId, ct).ConfigureAwait(false))
        {
            if (judgement.Move is not { UndoneAt: null })
            {
                continue;
            }

            // まとめて戻すのは誤判定ではないので、誤判定としては記録しない
            var outcome = await undo.UndoAsync(judgement.Id, asMisjudgement: false, ct).ConfigureAwait(false);
            switch (outcome.Result)
            {
                case UndoResult.Restored: restored++; break;
                case UndoResult.NotFound: notFound++; break;
                default: failed++; break;
            }
        }

        await store.TrialRuns.MarkRolledBackAsync(trialRunId, ct).ConfigureAwait(false);
        logger.LogInformation("お試し仕分け {Id} の移動をまとめて戻しました（戻した {Restored}、見つからない {NotFound}、失敗 {Failed}）。",
            trialRunId, restored, notFound, failed);
        return new RollbackSummary(restored, notFound, failed);
    }

    public async Task<TrialReport> GetReportAsync(long trialRunId, CancellationToken ct)
    {
        var run = await store.TrialRuns.GetAsync(trialRunId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"お試し仕分けの回 {trialRunId} が見つかりません。", nameof(trialRunId));
        var judgements = await LoadJudgementsAsync(trialRunId, ct).ConfigureAwait(false);
        return TrialReportBuilder.Build(run, judgements, config.Current.Items);
    }

    private async Task<List<JudgementRecord>> LoadJudgementsAsync(long trialRunId, CancellationToken ct)
    {
        var all = new List<JudgementRecord>();
        while (true)
        {
            var page = await store.Judgements.QueryAsync(
                new JudgementQuery { TrialRunId = trialRunId, Offset = all.Count, Limit = PageSize }, ct).ConfigureAwait(false);
            all.AddRange(page);
            if (page.Count < PageSize)
            {
                return all;
            }
        }
    }

    private async Task<List<FoundMail>> SearchAsync(TrialRequest request, CancellationToken ct)
    {
        var found = new List<FoundMail>();
        foreach (var accountId in request.Accounts)
        {
            if (!accounts.TryGetReader(accountId, out var reader))
            {
                throw new ArgumentException($"アカウント「{accountId}」が設定にありません。");
            }

            found.AddRange(await reader.SearchByDateAsync(request.Folder, request.From, request.To, ct).ConfigureAwait(false));
        }

        return found;
    }

    private static void Validate(TrialRequest request)
    {
        if (request.From > request.To)
        {
            throw new ArgumentException("期間の開始日が終了日より後になっています。");
        }

        if (request.Accounts.Count == 0)
        {
            throw new ArgumentException("アカウントを1つ以上選んでください。");
        }

        if (string.IsNullOrWhiteSpace(request.Folder))
        {
            throw new ArgumentException("対象フォルダーを指定してください。");
        }

        if (request.MaxMessages is < 1)
        {
            throw new ArgumentException("件数の上限は1以上で指定してください（空欄なら上限なし）。");
        }
    }
}
