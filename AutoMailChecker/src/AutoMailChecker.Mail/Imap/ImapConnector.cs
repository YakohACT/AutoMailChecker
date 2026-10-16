using System.Net.Sockets;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace AutoMailChecker.Mail.Imap;

/// <summary>
/// IMAP サーバーへの接続と認証（アプリ用パスワード）。993 番は最初から TLS、それ以外は STARTTLS を必須とし、平文では繋がない。
/// パスワードは資格情報マネージャーから接続のたびに読み、保持しない。
/// </summary>
public sealed class ImapConnector(ICredentialStore credentials)
{
    /// <summary>1つのコマンドの応答を待つ上限。</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(1);

    public async Task<ImapClient> ConnectAsync(AccountConfig account, CancellationToken ct)
    {
        var password = ReadPassword(account)
            ?? throw new MailServerException(account.Id,
                $"パスワードが登録されていません（資格情報マネージャーの「{account.EffectiveCredentialKey}」）。",
                isAuthenticationFailure: true);

        var client = new ImapClient { Timeout = (int)CommandTimeout.TotalMilliseconds };
        try
        {
            var security = account.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(account.Host, account.Port, security, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(account.User, password, ct).ConfigureAwait(false);
            return client;
        }
        catch (AuthenticationException ex)
        {
            client.Dispose();
            throw new MailServerException(account.Id,
                "認証に失敗しました。ユーザー名とアプリ用パスワードを確認してください。", isAuthenticationFailure: true, ex);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            client.Dispose();
            throw new MailServerException(account.Id, $"サーバーに接続できません: {ex.Message}", isAuthenticationFailure: false, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>資格情報マネージャーのパスワード。未登録なら null。</summary>
    public string? ReadPassword(AccountConfig account) =>
        credentials.ReadSecret(account.EffectiveCredentialKey) is { Length: > 0 } password ? password : null;

    /// <summary>繋ぎ直せば直る見込みのある失敗か（通信の切断、時間切れ、応答の乱れ）。</summary>
    public static bool IsConnectionFailure(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or SslHandshakeException
            or ImapProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException;
}
