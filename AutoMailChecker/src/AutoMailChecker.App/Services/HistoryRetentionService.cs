using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 毎日1回、保持期間（既定 90 日）を過ぎた判定履歴を消す。誤判定が記録されたもの（keep）は残す。
/// </summary>
public sealed class HistoryRetentionService(
    IStateStore store, IConfigProvider config, TimeProvider time, ILogger<HistoryRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                await PurgeAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 終了時
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            var days = config.Current.History.RetentionDays;
            var result = await store.PurgeExpiredAsync(days, ct).ConfigureAwait(false);
            if (result.Judgements > 0 || result.ProcessedMessages > 0)
            {
                logger.LogInformation(
                    "保持期間（{Days}日）を過ぎた記録を消しました。判定履歴 {Judgements} 件、処理済みメール {Processed} 件",
                    days, result.Judgements, result.ProcessedMessages);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "保持期間を過ぎた記録の削除に失敗しました。");
        }
    }
}
