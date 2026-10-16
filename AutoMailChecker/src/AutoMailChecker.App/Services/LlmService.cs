using AutoMailChecker.Core;
using AutoMailChecker.Llm;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// llama-server を起動し、落ちたら起動し直す。起動できない、または短い間に落ち続けるときは判定を保留し（処理待ちは溜まる）、
/// トレイで知らせて 30 分後にもう一度試す。終了時は llama-server を止めてメモリを空ける。
/// 運転モード（一時停止・完全停止・低負荷）による切り替えは開発ステップ5で足す。
/// </summary>
public sealed class LlmService(
    LlamaServerManager server, QueueSignal queueSignal, IStatusReporter status, TimeProvider time, ILogger<LlmService> logger)
    : BackgroundService
{
    public const string StatusSource = "AI";

    /// <summary>この回数続けて落ちたら、しばらく起動し直さない。</summary>
    private const int MaxConsecutiveCrashes = 3;

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CrashRestartDelay = TimeSpan.FromSeconds(10);

    /// <summary>これより長く動いていたら、落ちた回数を数え直す。</summary>
    private static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var crashes = 0;
        try
        {
            while (true)
            {
                try
                {
                    await server.StartAsync(LlmProfile.Normal, stoppingToken).ConfigureAwait(false);
                }
                catch (LlmStartException ex)
                {
                    logger.LogError("llama-server を起動できません。{Minutes} 分後にもう一度試します: {Message}",
                        RetryAfterFailure.TotalMinutes, ex.Message);
                    status.ReportError(StatusSource, $"llama-server を起動できません（判定保留中）: {ex.Message}");
                    await Task.Delay(RetryAfterFailure, time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                status.ClearError(StatusSource);
                queueSignal.Notify(); // 溜まっていた分の判定を始める

                var startedAt = time.GetTimestamp();
                await server.WaitForUnexpectedExitAsync(stoppingToken).ConfigureAwait(false);

                crashes = time.GetElapsedTime(startedAt) > StableUptime ? 1 : crashes + 1;
                if (crashes >= MaxConsecutiveCrashes)
                {
                    logger.LogError("llama-server が続けて {Crashes} 回終了しました。{Minutes} 分後にもう一度試します。",
                        crashes, RetryAfterFailure.TotalMinutes);
                    status.ReportError(StatusSource, "llama-server が終了を繰り返しています（判定保留中）");
                    await Task.Delay(RetryAfterFailure, time, stoppingToken).ConfigureAwait(false);
                    crashes = 0;
                }
                else
                {
                    status.ReportError(StatusSource, "llama-server が終了しました（起動し直しています）");
                    await Task.Delay(CrashRestartDelay, time, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 終了時
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // llama-server を止めてメモリと VRAM を空けてから終える
        await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
