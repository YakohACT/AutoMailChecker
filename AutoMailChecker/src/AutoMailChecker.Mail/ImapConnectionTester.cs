using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Mail.Imap;

namespace AutoMailChecker.Mail;

/// <summary>全般タブの「接続テスト」。接続・認証して受信トレイとフォルダーの数を確かめ、すぐ切る。</summary>
public sealed class ImapConnectionTester(ImapConnector connector) : IMailConnectionTester
{
    public async Task<string> TestAsync(AccountConfig account, CancellationToken ct)
    {
        using var client = await connector.ConnectAsync(account, ct).ConfigureAwait(false);
        try
        {
            var folders = 0;
            foreach (var personal in client.PersonalNamespaces)
            {
                folders += (await client.GetFoldersAsync(personal, subscribedOnly: false, ct).ConfigureAwait(false)).Count;
            }

            await client.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            return Translate.Text(
                $"接続できました（受信トレイ {client.Inbox.Count} 通、フォルダー {folders} 個）。",
                $"Connected (inbox: {client.Inbox.Count} mails, folders: {folders}).");
        }
        catch (Exception ex) when (ImapConnector.IsConnectionFailure(ex))
        {
            throw new MailServerException(account.Id, Translate.Text(
                $"接続のあとで通信が切れました: {ex.Message}",
                $"The connection dropped right after connecting: {ex.Message}"), isAuthenticationFailure: false, ex);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ImapConnector.IsConnectionFailure(ex))
                {
                }
            }
        }
    }
}
