using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Mail.Imap;
using AutoMailChecker.Mail.Watching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 設定の全アカウントについて AccountWatcher を動かす。運転モードに関係なく監視は続け、新着は処理待ちに積む。
/// アカウントは起動時の設定で決まる（追加・削除は再起動で反映）。
/// </summary>
public sealed class MailWatchService(
    IConfigProvider config, ImapConnector connector, IStateStore store, QueueSignal queueSignal,
    IStatusReporter status, TimeProvider time, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<MailWatchService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var accounts = config.Current.Accounts;
        if (accounts.Count == 0)
        {
            _logger.LogWarning("監視するアカウントが設定にありません。");
            return;
        }

        var watcherLogger = loggerFactory.CreateLogger<AccountWatcher>();
        var watchers = accounts
            .Select(account => new AccountWatcher(account, connector, store, queueSignal, status, time, watcherLogger))
            .ToList();
        await Task.WhenAll(watchers.Select(w => RunAsync(w, stoppingToken))).ConfigureAwait(false);
    }

    private async Task RunAsync(AccountWatcher watcher, CancellationToken ct)
    {
        try
        {
            await watcher.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 終了時
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "{Account}: 監視が止まりました。", watcher.AccountId);
            status.ReportError(watcher.AccountId, "監視が止まりました（ログを確認してください）");
        }
    }
}
