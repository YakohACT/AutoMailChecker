using AutoMailChecker.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 運転モードに合わせて llama-server を起動・終了・切り替える（シーケンス SQ-2）。
/// - 稼働中: 通常（GPU 使用）で起動 / 低負荷で継続: CPU のみ・2 スレッドで起動し直す
/// - 判定保留: そのまま（モデルを読み直さないので再開が速い） / 完全停止: 終了してメモリと VRAM を空ける
/// 切り替えは処理中の1通が終わってから行う。起動に失敗したり、落ち続けたりしたら判定を保留して知らせ、30 分後にもう一度試す。
/// 接続先 URL の起動済みサーバーを使うときは、起動・終了の代わりに繋ぐ・要求を止めるだけにし、繋がらなければ 1 分後にもう一度試す
/// （サーバーを起動するのは利用者なので、起動したらすぐ使えるようにする）。
/// </summary>
public sealed class LlmService(
    ILlmRuntime server, IRunModeSource runModes, ProcessingGate gate, QueueSignal queueSignal,
    IStatusReporter status, TimeProvider time, ILogger<LlmService> logger) : BackgroundService
{
    public const string StatusSource = "AI";

    /// <summary>この回数続けて落ちたら、しばらく起動し直さない。</summary>
    private const int MaxConsecutiveCrashes = 3;

    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan RetryAfterExternalFailure = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CrashRestartDelay = TimeSpan.FromSeconds(10);

    /// <summary>これより長く動いていたら、落ちた回数を数え直す。</summary>
    private static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource _modeChanged = new();
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private long _startedAt;
    private int _crashes;

    /// <summary>
    /// 予期しない終了（切断）を待つか。起動（接続）できたら true、終了を知らせたら false にする。
    /// 知らせ終わった終了をもう一度待つと、完了済みのままなので待たずに戻り、ループが空回りする。
    /// </summary>
    private bool _watchingExit;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        runModes.Changed += OnModeChanged;
        try
        {
            while (true)
            {
                var retryIn = await ReconcileAsync(stoppingToken).ConfigureAwait(false);

                // 運転モードが変わるか、llama-server が落ちるか、再試行の時刻になるまで待つ
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var exited = _watchingExit
                    ? server.WaitForUnexpectedExitAsync(iteration.Token)
                    : Task.Delay(Timeout.Infinite, iteration.Token);
                var woken = _wake.WaitAsync(retryIn ?? Timeout.InfiniteTimeSpan, iteration.Token);
                var first = await Task.WhenAny(exited, woken).ConfigureAwait(false);
                stoppingToken.ThrowIfCancellationRequested();
                await iteration.CancelAsync().ConfigureAwait(false);

                if (first == exited && exited.IsCompletedSuccessfully)
                {
                    _watchingExit = false;
                    OnCrashed();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 終了時
        }
        finally
        {
            runModes.Changed -= OnModeChanged;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // llama-server を止めてメモリと VRAM を空けてから終える（接続先 URL のサーバーは止めない）
        await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>いまの運転モードに合わせて AI を準備・後始末する。次に試すまでの時間を返す（待つ必要がなければ null）。</summary>
    internal async Task<TimeSpan?> ReconcileAsync(CancellationToken ct)
    {
        var mode = runModes.Current.Mode;
        var running = server.Running;
        LlmProfile? desired = mode switch
        {
            RunMode.Running => LlmProfile.Normal,
            RunMode.LowLoad => LlmProfile.LowLoad,
            RunMode.Stopped => null,
            _ => running, // 判定保留: 起動したまま（止まっていれば止まったまま）
        };
        if (desired == running)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (desired is not null && now < _retryAt)
        {
            return _retryAt - now; // 起動に失敗して待っているところ
        }

        // 処理中の1通が終わるのを待ってから切り替える（新しい1通は取らせない）
        using (await gate.HoldAsync(ct).ConfigureAwait(false))
        {
            if (desired is not { } profile)
            {
                _watchingExit = false;
                await server.StopAsync(ct).ConfigureAwait(false);
                logger.LogInformation(server.IsExternal
                        ? "完全停止のため AI サーバーへの要求を止めました（サーバーは起動したままです。{Reason}）。"
                        : "完全停止のため llama-server を終了しました（{Reason}）。",
                    runModes.Current.Reason ?? "");
                return null;
            }

            // 読み込み中に運転モードが変わったら（ゲームを起動した、など）、待たずに打ち切る
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _modeChanged.Token);
            try
            {
                await server.StartAsync(profile, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogInformation(server.IsExternal
                    ? "AI サーバーへの接続中に運転モードが変わったので、接続を取りやめました。"
                    : "llama-server の起動中に運転モードが変わったので、起動を取りやめました。");
                return null;
            }
            catch (LlmStartException ex)
            {
                var retry = RetryDelay;
                _retryAt = time.GetUtcNow() + retry;
                logger.LogError(server.IsExternal
                        ? "AI サーバーに接続できません。{Minutes} 分後にもう一度試します: {Message}"
                        : "llama-server を起動できません。{Minutes} 分後にもう一度試します: {Message}",
                    retry.TotalMinutes, ex.Message);
                status.ReportError(StatusSource, server.IsExternal
                    ? $"AI サーバーに接続できません（判定保留中）: {ex.Message}"
                    : $"llama-server を起動できません（判定保留中）: {ex.Message}");
                return retry;
            }
        }

        _retryAt = DateTimeOffset.MinValue;
        _startedAt = time.GetTimestamp();
        _watchingExit = true;
        status.ClearError(StatusSource);
        queueSignal.Notify(); // 溜まっていた分の判定を始める
        return null;
    }

    /// <summary>準備に失敗したあと、もう一度試すまでの時間。</summary>
    private TimeSpan RetryDelay => server.IsExternal ? RetryAfterExternalFailure : RetryAfterFailure;

    private void OnCrashed()
    {
        _crashes = time.GetElapsedTime(_startedAt) > StableUptime ? 1 : _crashes + 1;
        if (_crashes >= MaxConsecutiveCrashes)
        {
            var retry = RetryDelay;
            logger.LogError(server.IsExternal
                    ? "AI サーバーとの接続が続けて {Crashes} 回切れました。{Minutes} 分後にもう一度試します。"
                    : "llama-server が続けて {Crashes} 回終了しました。{Minutes} 分後にもう一度試します。",
                _crashes, retry.TotalMinutes);
            status.ReportError(StatusSource, server.IsExternal
                ? "AI サーバーとの接続が切れ続けています（判定保留中）"
                : "llama-server が終了を繰り返しています（判定保留中）");
            _retryAt = time.GetUtcNow() + retry;
            _crashes = 0;
        }
        else
        {
            status.ReportError(StatusSource, server.IsExternal
                ? "AI サーバーとの接続が切れました（繋ぎ直しています）"
                : "llama-server が終了しました（起動し直しています）");
            _retryAt = time.GetUtcNow() + CrashRestartDelay;
        }
    }

    internal void OnModeChanged(object? sender, RunModeState state)
    {
        // 起動中なら打ち切らせ、待っているループを起こす
        // 古いものは起動側がまだ参照しているかもしれないので Dispose しない（タイマーを持たないので後始末は要らない）
        Interlocked.Exchange(ref _modeChanged, new CancellationTokenSource()).Cancel();
        _retryAt = DateTimeOffset.MinValue;
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
