using AutoMailChecker.Core;
using AutoMailChecker.Mail.Imap;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace AutoMailChecker.Mail;

/// <summary>
/// IMailReader の IMAP 実装。読み取りはすべて BODY.PEEK で行い、既読にしない。
/// フォルダーは読み取り専用（EXAMINE）で開く。
/// </summary>
public sealed class ImapMailReader(ImapAccountSession session, BodyPreprocessor preprocessor, TimeProvider time) : IMailReader
{
    // UID FETCH (ENVELOPE INTERNALDATE)。ENVELOPE に差出人・件名・日付・Message-ID が含まれる
    private static readonly FetchRequest HeaderRequest =
        new(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate);

    private static readonly FetchRequest DateRequest = new(MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate);

    private static readonly FetchRequest SnapshotRequest =
        new(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate
            | MessageSummaryItems.BodyStructure);

    public string AccountId => session.AccountId;

    public Task<MailHeader> FetchHeaderAsync(MailRef mail, CancellationToken ct) =>
        session.UseAsync(async (client, token) =>
        {
            var (_, summary) = await FetchSummaryAsync(client, mail, HeaderRequest, token).ConfigureAwait(false);
            return ToHeader(mail, summary);
        }, retryOnDisconnect: true, ct);

    public Task<MailSnapshot> FetchSnapshotAsync(MailRef mail, int maxBodyChars, CancellationToken ct) =>
        session.UseAsync(async (client, token) =>
        {
            var (folder, summary) = await FetchSummaryAsync(client, mail, SnapshotRequest, token).ConfigureAwait(false);
            var uid = new UniqueId(mail.Uid);

            // text/plain を優先し、なければ text/html の1パートだけを取る。添付ファイルは名前だけで中身は取らない
            var plain = summary.TextBody is { } textPart
                ? await GetTextAsync(folder, uid, textPart, token).ConfigureAwait(false)
                : null;
            var html = string.IsNullOrWhiteSpace(plain) && summary.HtmlBody is { } htmlPart
                ? await GetTextAsync(folder, uid, htmlPart, token).ConfigureAwait(false)
                : null;

            var body = preprocessor.ToPlainText(plain, html, maxBodyChars);
            var attachments = BodyPreprocessor.LimitAttachmentNames(summary.Attachments.Select(a => a.FileName ?? ""));
            return new MailSnapshot(ToHeader(mail, summary), body, attachments);
        }, retryOnDisconnect: true, ct);

    public Task<IReadOnlyList<FoundMail>> SearchByDateAsync(string folderName, DateOnly from, DateOnly to, CancellationToken ct) =>
        session.UseAsync<IReadOnlyList<FoundMail>>(async (client, token) =>
        {
            var folder = await ImapFolders.OpenAsync(client, folderName, FolderAccess.ReadOnly, token).ConfigureAwait(false);
            // UID SEARCH SINCE ＜開始日＞ BEFORE ＜終了日の翌日＞（サーバー側の受信日で比べる）
            var query = SearchQuery.DeliveredAfter(from.ToDateTime(TimeOnly.MinValue))
                .And(SearchQuery.DeliveredBefore(to.AddDays(1).ToDateTime(TimeOnly.MinValue)));
            var uids = await folder.SearchAsync(query, token).ConfigureAwait(false);
            if (uids.Count == 0)
            {
                return [];
            }

            // 間引くときに受信日時順に並べるので、受信日時（INTERNALDATE）も取る
            var summaries = await folder.FetchAsync(uids, DateRequest, token).ConfigureAwait(false);
            return summaries
                .Select(s => new FoundMail(
                    new MailRef(AccountId, folderName, folder.UidValidity, s.UniqueId.Id),
                    s.InternalDate ?? DateTimeOffset.MinValue))
                .ToList();
        }, retryOnDisconnect: true, ct);

    public Task<MailRef?> FindByMessageIdAsync(string folderName, string messageId, CancellationToken ct) =>
        session.UseAsync<MailRef?>(async (client, token) =>
        {
            IMailFolder folder;
            try
            {
                folder = await ImapFolders.OpenAsync(client, folderName, FolderAccess.ReadOnly, token).ConfigureAwait(false);
            }
            catch (FolderNotFoundException)
            {
                return null;
            }

            // UID SEARCH HEADER Message-ID ＜値＞
            var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), token).ConfigureAwait(false);
            return found.Count > 0 ? new MailRef(AccountId, folderName, folder.UidValidity, found[^1].Id) : null;
        }, retryOnDisconnect: true, ct);

    public Task<IReadOnlyList<MailFolderInfo>> ListFoldersAsync(CancellationToken ct) =>
        session.UseAsync<IReadOnlyList<MailFolderInfo>>(async (client, token) =>
        {
            var folders = new List<IMailFolder>();
            foreach (var personal in client.PersonalNamespaces)
            {
                folders.AddRange(await client.GetFoldersAsync(personal, subscribedOnly: false, token).ConfigureAwait(false));
            }

            return folders
                .Select(folder => ImapFolders.Describe(client, folder))
                .DistinctBy(info => info.Name)
                .OrderBy(info => info.Name, StringComparer.Ordinal)
                .ToList();
        }, retryOnDisconnect: true, ct);

    private static async Task<(IMailFolder Folder, IMessageSummary Summary)> FetchSummaryAsync(
        ImapClient client, MailRef mail, FetchRequest request, CancellationToken ct)
    {
        IMailFolder folder;
        try
        {
            folder = await ImapFolders.OpenAsync(client, mail.Folder, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        }
        catch (FolderNotFoundException)
        {
            throw new MailNotFoundException(mail, "フォルダーがサーバーにありません。");
        }

        if (folder.UidValidity != mail.UidValidity)
        {
            throw new MailNotFoundException(mail, "UIDVALIDITY が変わったため、この UID は無効です。");
        }

        var summaries = await folder.FetchAsync([new UniqueId(mail.Uid)], request, ct).ConfigureAwait(false);
        var summary = summaries.FirstOrDefault()
            ?? throw new MailNotFoundException(mail, "メールがフォルダーにありません（移動または削除された）。");
        return (folder, summary);
    }

    private static async Task<string?> GetTextAsync(IMailFolder folder, UniqueId uid, BodyPart part, CancellationToken ct) =>
        await folder.GetBodyPartAsync(uid, part, ct).ConfigureAwait(false) is TextPart text ? text.Text : null;

    private MailHeader ToHeader(MailRef mail, IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var from = envelope?.From.Mailboxes.FirstOrDefault()?.ToString() ?? "";
        // 受信日時はサーバーが受け取った日時（INTERNALDATE）。なければ Date ヘッダー
        var received = summary.InternalDate ?? envelope?.Date ?? time.GetUtcNow();
        return new MailHeader(mail, envelope?.MessageId ?? "", from, envelope?.Subject ?? "", received);
    }
}
