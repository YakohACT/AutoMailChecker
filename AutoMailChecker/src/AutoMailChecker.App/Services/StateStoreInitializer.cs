using AutoMailChecker.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 起動時に状態 DB を最新の版へ移行し、処理中のまま残った行を処理待ちに戻す。
/// ほかのサービスより先に動くよう、最初に登録する。
/// </summary>
public sealed class StateStoreInitializer(
    IStateStore store, AppStatusService status, ILogger<StateStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var pending = await store.Queue.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        status.SetPendingCount(pending);
        logger.LogInformation("状態 DB を開きました。未処理 {Pending} 件", pending);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
