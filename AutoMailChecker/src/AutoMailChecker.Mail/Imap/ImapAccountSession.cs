using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Mail.Imap;

/// <summary>
/// 本文の取得と移動に使う、必要なときだけ開く接続（アカウントごとに1つ）。5 分使わなければ閉じる。
/// 監視用の接続は IDLE 中に他のコマンドを送れないため、それとは別に持つ（外部仕様タブ）。
/// 1つの接続を同時に使えるのは1つの処理だけ。
/// </summary>
public sealed class ImapAccountSession : IDisposable
{
    public static readonly TimeSpan CloseWhenUnusedFor = TimeSpan.FromMinutes(5);

    private readonly AccountConfig _account;
    private readonly ImapConnector _connector;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITimer _closeTimer;
    private ImapClient? _client;
    private bool _disposed;

    public ImapAccountSession(AccountConfig account, ImapConnector connector, TimeProvider time, ILogger logger)
    {
        _account = account;
        _connector = connector;
        _logger = logger;
        _closeTimer = time.CreateTimer(_ => _ = CloseIfUnusedAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string AccountId => _account.Id;

    /// <summary>
    /// 接続を使って処理する。通信が切れていたら繋ぎ直す。
    /// </summary>
    /// <param name="retryOnDisconnect">
    /// true なら、処理の途中で切れたときに1回だけ繋ぎ直してやり直す（しばらく使わない間にサーバー側で切られていることがあるため）。
    /// 読み取りのように、やり直しても結果が変わらない処理だけで true にする。
    /// </param>
    public async Task<T> UseAsync<T>(
        Func<ImapClient, CancellationToken, Task<T>> action, bool retryOnDisconnect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var attempt = 1; ; attempt++)
            {
                var client = _client is { IsConnected: true, IsAuthenticated: true }
                    ? _client
                    : await ReconnectAsync(ct).ConfigureAwait(false);
                try
                {
                    return await action(client, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ImapConnector.IsConnectionFailure(ex) && !ct.IsCancellationRequested)
                {
                    Drop();
                    if (!retryOnDisconnect || attempt >= 2)
                    {
                        throw new MailServerException(_account.Id, Translate.Text(
                            $"サーバーとの通信が切れました: {ex.Message}",
                            $"The connection to the server dropped: {ex.Message}"),
                            isAuthenticationFailure: false, ex);
                    }

                    _logger.LogDebug("{Account}: 通信が切れていたので繋ぎ直します: {Message}", _account.Id, ex.Message);
                }
            }
        }
        finally
        {
            if (!_disposed)
            {
                _closeTimer.Change(CloseWhenUnusedFor, Timeout.InfiniteTimeSpan);
            }

            _gate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _closeTimer.Dispose();
        Drop();
    }

    private async Task<ImapClient> ReconnectAsync(CancellationToken ct)
    {
        Drop();
        _client = await _connector.ConnectAsync(_account, ct).ConfigureAwait(false);
        return _client;
    }

    private void Drop()
    {
        _client?.Dispose();
        _client = null;
    }

    private async Task CloseIfUnusedAsync()
    {
        // 使用中なら閉じない（使い終わったときにタイマーがかけ直される）
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (_client is { } client)
            {
                _client = null;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.DisconnectAsync(quit: true, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException || ImapConnector.IsConnectionFailure(ex))
                {
                    // 閉じるだけなので失敗しても構わない
                }
                finally
                {
                    client.Dispose();
                }

                _logger.LogDebug("{Account}: しばらく使わなかったので読み取り用の接続を閉じました。", _account.Id);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
