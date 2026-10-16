using AutoMailChecker.Core;
using MailKit;
using MailKit.Net.Imap;

namespace AutoMailChecker.Mail.Imap;

internal static class ImapFolders
{
    /// <summary>設定のフォルダー名からフォルダーを引く。なければ <see cref="FolderNotFoundException"/>。</summary>
    public static async Task<IMailFolder> GetAsync(ImapClient client, string name, CancellationToken ct) =>
        AppInfo.IsInbox(name) ? client.Inbox : await client.GetFolderAsync(name, ct).ConfigureAwait(false);

    /// <summary>必要な権限で開いていなければ開く（読み取りだけなら EXAMINE で開く）。</summary>
    public static async Task<IMailFolder> OpenAsync(ImapClient client, string name, FolderAccess access, CancellationToken ct)
    {
        var folder = await GetAsync(client, name, ct).ConfigureAwait(false);
        if (!folder.IsOpen || folder.Access < access)
        {
            await folder.OpenAsync(access, ct).ConfigureAwait(false);
        }

        return folder;
    }

    /// <summary>迷惑メールフォルダー（SPECIAL-USE の \Junk）か。迷惑メールの判定はサーバーに任せ、監視も振り分けもしない。</summary>
    public static bool IsJunk(ImapClient client, IMailFolder folder)
    {
        if (folder.Attributes.HasFlag(FolderAttributes.Junk))
        {
            return true;
        }

        var supportsSpecialUse = (client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0;
        var junk = supportsSpecialUse ? client.GetFolder(SpecialFolder.Junk) : null;
        return junk is not null && string.Equals(junk.FullName, folder.FullName, StringComparison.Ordinal);
    }
}
