using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Windows.Pause;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 停止対象を定期的に調べ（既定 5 秒ごと）、運転モードをトレイに出す。
/// 最初の運転モードは起動時に決めてから AI を起動する（ゲーム中に起動したとき、モデルを読み込まないように）。
/// </summary>
public sealed class RunModeService(
    PauseController pause, IConfigProvider config, AppStatusService status, QueueSignal queueSignal,
    TimeProvider time, ILogger<RunModeService> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        pause.Changed += OnChanged;
        Poll();
        Show(pause.Current);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        pause.Changed -= OnChanged;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                // 間隔は設定の変更をすぐ反映する（2〜60 秒）
                var interval = TimeSpan.FromSeconds(Math.Clamp(config.Current.Pause.PollSeconds, 2, 60));
                await Task.Delay(interval, time, stoppingToken).ConfigureAwait(false);
                Poll();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 終了時
        }
    }

    private void Poll()
    {
        try
        {
            pause.Poll();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "運転モードの判定に失敗しました。");
        }
    }

    private void OnChanged(object? sender, RunModeState state)
    {
        Show(state);
        queueSignal.Notify(); // 再開したら、溜まっていた分の処理を始める
    }

    private void Show(RunModeState state) => status.SetMode(state.Mode, state.Reason, state.HeldUntil);
}
