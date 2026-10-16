using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Mail.Imap;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Mail.Watching;

/// <summary>
/// 1アカウントの新着を監視し、処理待ちに積む（1アカウント1インスタンス、接続も1本）。
/// - IMAP IDLE で新着を待つ。9 分ごとに IDLE を張り直す（RFC 2177 の上限 29 分より短くし、途中切断に備える）
/// - IDLE が使えなければ 5 分ごとに確認する
/// - 切断・時間切れは指数バックオフ（5 秒→最大 5 分）で繋ぎ直し、繋がったら UID で取りこぼしを回収する
/// - 認証に失敗したら、資格情報マネージャーのパスワードが変わるまでこのアカウントだけ止める
/// 監視用の接続は読み取り専用（EXAMINE）で開き、メールを変更しない。
/// </summary>
public sealed class AccountWatcher
{
    public static readonly TimeSpan IdleRestartInterval = TimeSpan.FromMinutes(9);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CredentialCheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FolderErrorRetryInterval = TimeSpan.FromMinutes(30);

    /// <summary>この回数続けて繋がらなかったらトレイに出す（一時的な切断では知らせない）。</summary>
    private const int FailuresBeforeReport = 3;

    private readonly AccountConfig _account;
    private readonly ImapConnector _connector;
    private readonly FolderSynchronizer _synchronizer;
    private readonly QueueSignal _queueSignal;
    private readonly IStatusReporter _status;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private volatile bool _changed;
    private CancellationTokenSource? _idleDone;

    public AccountWatcher(
        AccountConfig account, ImapConnector connector, IStateStore store, QueueSignal queueSignal,
        IStatusReporter status, TimeProvider time, ILogger<AccountWatcher> logger)
    {
        _account = account;
        _connector = connector;
        _synchronizer = new FolderSynchronizer(store, time, logger);
        _queueSignal = queueSignal;
        _status = status;
        _time = time;
        _logger = logger;
    }

    public string AccountId => _account.Id;

    /// <summary>止める指示（ct）が来るまで監視を続ける。止めたときは OperationCanceledException を投げる。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = await _connector.ConnectAsync(_account, ct).ConfigureAwait(false);
                failures = 0;
                _status.ClearError(AccountId);
                _logger.LogInformation("{Account}: 接続しました（IDLE {Idle}）。", AccountId,
                    client.Capabilities.HasFlag(ImapCapabilities.Idle) ? "あり" : "なし。5分ごとに確認します");
                await WatchAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (MailServerException ex) when (ex.IsAuthenticationFailure)
            {
                _logger.LogWarning("{Account}: {Message}パスワードが変わるまで監視を止めます。", AccountId, ex.Message);
                _status.ReportError(AccountId, ex.Message);
                await WaitForCredentialChangeAsync(ct).ConfigureAwait(false);
            }
            catch (WatchFolderException ex)
            {
                _logger.LogWarning("{Account}: {Message}", AccountId, ex.Message);
                _status.ReportError(AccountId, ex.Message);
                await Task.Delay(FolderErrorRetryInterval, _time, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MailServerException || ImapConnector.IsConnectionFailure(ex))
            {
                failures++;
                var delay = Backoff(failures);
                _logger.LogWarning("{Account}: 接続が切れました。{Seconds} 秒後に繋ぎ直します（{Failures} 回目）: {Message}",
                    AccountId, delay.TotalSeconds, failures, ex.Message);
                if (failures >= FailuresBeforeReport)
                {
                    _status.ReportError(AccountId, "サーバーに接続できません（繋ぎ直しています）");
                }

                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogError(ex, "{Account}: 監視中に想定外のエラーが起きました。繋ぎ直します。", AccountId);
                await Task.Delay(Backoff(failures), _time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>繋ぎ直すまでの待ち時間。5 秒から倍々で、最大 5 分。</summary>
    internal static TimeSpan Backoff(int failures)
    {
        var seconds = 5 * Math.Pow(2, Math.Clamp(failures - 1, 0, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, 300));
    }

    private async Task WatchAsync(ImapClient client, CancellationToken ct)
    {
        var folders = await ResolveWatchFoldersAsync(client, ct).ConfigureAwait(false);
        // IDLE で見張るのは最初のフォルダー（受信トレイ）。ほかのフォルダーは張り直しのたびに確認する
        var primary = folders[0];
        var others = folders.Skip(1).ToList();
        var canIdle = client.Capabilities.HasFlag(ImapCapabilities.Idle);

        primary.Folder.CountChanged += OnCountChanged;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                _changed = false;
                foreach (var folder in others)
                {
                    await SyncAsync(folder, ct).ConfigureAwait(false);
                }

                // IDLE は最後に開いたフォルダーに対して行うので、見張るフォルダーを最後に開く
                await SyncAsync(primary, ct).ConfigureAwait(false);

                if (_changed)
                {
                    continue; // 確認している間に届いた
                }

                if (canIdle)
                {
                    await IdleAsync(client, ct).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
                    await client.NoOpAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            primary.Folder.CountChanged -= OnCountChanged;
        }
    }

    private async Task IdleAsync(ImapClient client, CancellationToken ct)
    {
        using var done = new CancellationTokenSource(IdleRestartInterval, _time);
        Volatile.Write(ref _idleDone, done);
        try
        {
            if (!_changed)
            {
                await client.IdleAsync(done.Token, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _idleDone, null);
        }
    }

    private void OnCountChanged(object? sender, EventArgs e)
    {
        // 新着（EXISTS）を受けたら IDLE を抜けて確認する
        _changed = true;
        try
        {
            Volatile.Read(ref _idleDone)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task SyncAsync(WatchTarget target, CancellationToken ct)
    {
        if (!target.Folder.IsOpen)
        {
            await target.Folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        }

        var added = await _synchronizer.SyncAsync(AccountId, new MailKitWatchedFolder(target.Name, target.Folder), ct)
            .ConfigureAwait(false);
        if (added > 0)
        {
            _logger.LogInformation("{Account} {Folder}: 新着 {Count} 通を処理待ちに積みました。", AccountId, target.Name, added);
            _queueSignal.Notify();
        }
    }

    private async Task<IReadOnlyList<WatchTarget>> ResolveWatchFoldersAsync(ImapClient client, CancellationToken ct)
    {
        var targets = new List<WatchTarget>();
        var problems = new List<string>();
        foreach (var name in _account.WatchFolders.Distinct(StringComparer.Ordinal))
        {
            IMailFolder folder;
            try
            {
                folder = await ImapFolders.GetAsync(client, name, ct).ConfigureAwait(false);
            }
            catch (FolderNotFoundException)
            {
                problems.Add($"監視するフォルダー「{name}」がサーバーにありません。");
                continue;
            }

            if (ImapFolders.IsJunk(client, folder))
            {
                problems.Add($"迷惑メールフォルダー「{name}」は監視できません。");
                continue;
            }

            targets.Add(new WatchTarget(name, folder));
        }

        if (targets.Count == 0)
        {
            throw new WatchFolderException(problems.Count > 0 ? string.Join(" ", problems) : "監視するフォルダーがありません。");
        }

        if (problems.Count > 0)
        {
            // 使えるフォルダーだけで監視を続け、使えないものはトレイで知らせる
            _logger.LogWarning("{Account}: {Problems}", AccountId, string.Join(" ", problems));
            _status.ReportError(AccountId, string.Join(" ", problems));
        }

        // 受信トレイがあれば、それを IDLE で見張る
        return targets.OrderByDescending(t => AppInfo.IsInbox(t.Name)).ToList();
    }

    private async Task WaitForCredentialChangeAsync(CancellationToken ct)
    {
        var failed = _connector.ReadPassword(_account);
        while (true)
        {
            await Task.Delay(CredentialCheckInterval, _time, ct).ConfigureAwait(false);
            var current = _connector.ReadPassword(_account);
            if (current is not null && current != failed)
            {
                _logger.LogInformation("{Account}: パスワードが変わったので繋ぎ直します。", AccountId);
                return;
            }
        }
    }

    private sealed record WatchTarget(string Name, IMailFolder Folder);
}

/// <summary>監視するフォルダーが使えない（存在しない、迷惑メールフォルダー）。設定を直すまで繋ぎ直しても直らない。</summary>
internal sealed class WatchFolderException(string message) : Exception(message);
