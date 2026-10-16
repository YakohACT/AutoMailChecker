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

    /// <summary>送信済み・下書き・ごみ箱・すべてのメールなど、振り分け先に向かない特別なフォルダーの属性。</summary>
    private const FolderAttributes SpecialAttributes = FolderAttributes.All | FolderAttributes.Drafts | FolderAttributes.Flagged
        | FolderAttributes.Important | FolderAttributes.Sent | FolderAttributes.Trash;

    private static readonly SpecialFolder[] SpecialFolders =
        [SpecialFolder.All, SpecialFolder.Drafts, SpecialFolder.Flagged, SpecialFolder.Important, SpecialFolder.Sent, SpecialFolder.Trash];

    /// <summary>設定画面の振り分け先の候補を選ぶための、フォルダーの種類。</summary>
    public static MailFolderInfo Describe(ImapClient client, IMailFolder folder)
    {
        var supportsSpecialUse = (client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0;
        var isSpecial = (folder.Attributes & SpecialAttributes) != 0
            || (supportsSpecialUse && SpecialFolders.Any(s => client.GetFolder(s)?.FullName == folder.FullName));
        return new MailFolderInfo(
            folder.FullName,
            IsInbox: AppInfo.IsInbox(folder.FullName) || folder.Attributes.HasFlag(FolderAttributes.Inbox),
            IsJunk: IsJunk(client, folder),
            IsSpecial: isSpecial,
            CanSelect: (folder.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0);
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
