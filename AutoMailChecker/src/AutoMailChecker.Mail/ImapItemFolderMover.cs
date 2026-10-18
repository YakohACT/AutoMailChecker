using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Mail.Imap;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

namespace AutoMailChecker.Mail;

/// <summary>
/// IItemFolderMover の IMAP 実装（アカウントごとに1つ）。メールを変更するのはこの2つの操作だけ。
/// - 移動先は設定の「項目 → このアカウントのフォルダー」の対応表から引く。外から移動先を指定する手段はない
/// - 同じ接続の中の MOVE（なければ COPY ＋ 削除フラグ ＋ 当該 UID だけの UID EXPUNGE）で完結させ、
///   アカウントをまたぐ移動やメールのダウンロード・追加（APPEND）はしない
/// - 既読などのフラグは変えない（MOVE と COPY はフラグをそのまま保つ）
/// </summary>
public sealed class ImapItemFolderMover(ImapAccountSession session, IConfigProvider config) : IItemFolderMover
{
    public string AccountId => session.AccountId;

    public Task<MovedMailRef> MoveToItemFolderAsync(MailRef mail, ItemId item, CancellationToken ct)
    {
        var folder = config.Current.Items.FirstOrDefault(i => i.Id == item)?.FolderFor(AccountId)
            ?? throw new MailFolderNotFoundException(AccountId, $"（項目「{item.Value}」の振り分け先が未設定）");
        return MoveAsync(mail, folder, ct);
    }

    public Task<UndoResult> MoveBackToInboxAsync(MovedMailRef moved, string? messageId, CancellationToken ct)
    {
        EnsureSameAccount(moved.AccountId);
        return session.UseAsync(async (client, token) =>
        {
            IMailFolder folder;
            try
            {
                folder = await ImapFolders.OpenAsync(client, moved.ToFolder, FolderAccess.ReadWrite, token).ConfigureAwait(false);
            }
            catch (FolderNotFoundException)
            {
                return UndoResult.NotFound;
            }

            // 移動時に記録した UID（COPYUID）で探し、使えなければ Message-ID で探す
            UniqueId? uid = null;
            if (moved.DestUid is { } destUid && moved.DestUidValidity == folder.UidValidity)
            {
                var found = await folder.SearchAsync(SearchQuery.Uids([new UniqueId(destUid)]), token).ConfigureAwait(false);
                uid = found.Count > 0 ? found[0] : null;
            }

            if (uid is null && !string.IsNullOrEmpty(messageId))
            {
                var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), token).ConfigureAwait(false);
                uid = found.Count > 0 ? found[^1] : null;
            }

            if (uid is null)
            {
                return UndoResult.NotFound; // すでに別の場所へ移されていた。何もしない
            }

            await MoveUidAsync(client, folder, uid.Value, client.Inbox, token).ConfigureAwait(false);
            return UndoResult.Restored;
        }, retryOnDisconnect: false, ct);
    }

    private Task<MovedMailRef> MoveAsync(MailRef mail, string destinationName, CancellationToken ct)
    {
        EnsureSameAccount(mail.AccountId);
        if (AppInfo.IsInbox(destinationName))
        {
            throw new MailMoveException("受信トレイは振り分け先にできません。");
        }

        return session.UseAsync(async (client, token) =>
        {
            IMailFolder source;
            try
            {
                source = await ImapFolders.OpenAsync(client, mail.Folder, FolderAccess.ReadWrite, token).ConfigureAwait(false);
            }
            catch (FolderNotFoundException)
            {
                throw new MailNotFoundException(mail, "フォルダーがサーバーにありません。");
            }

            if (source.UidValidity != mail.UidValidity)
            {
                throw new MailNotFoundException(mail, "UIDVALIDITY が変わったため、この UID は無効です。");
            }

            IMailFolder destination;
            try
            {
                destination = await ImapFolders.GetAsync(client, destinationName, token).ConfigureAwait(false);
            }
            catch (FolderNotFoundException)
            {
                throw new MailFolderNotFoundException(AccountId, destinationName);
            }

            if (ImapFolders.IsJunk(client, destination))
            {
                throw new MailMoveException($"迷惑メールフォルダー「{destinationName}」には移動しません。");
            }

            var uid = new UniqueId(mail.Uid);
            var exists = await source.SearchAsync(SearchQuery.Uids([uid]), token).ConfigureAwait(false);
            if (exists.Count == 0)
            {
                throw new MailNotFoundException(mail, "メールがフォルダーにありません（移動または削除された）。");
            }

            var destinationUid = await MoveUidAsync(client, source, uid, destination, token).ConfigureAwait(false);
            return new MovedMailRef(
                AccountId, mail.Folder, destinationName,
                destinationUid is null ? null : destination.UidValidity,
                destinationUid?.Id);
        }, retryOnDisconnect: false, ct);
    }

    /// <summary>
    /// UID MOVE（応答の COPYUID を返す）。MOVE に対応していなければ UID COPY → UID STORE +FLAGS.SILENT (\Deleted) →
    /// UID EXPUNGE（当該 UID のみ）。UID EXPUNGE には UIDPLUS が要るので、どちらもなければ移動しない。
    /// </summary>
    private static async Task<UniqueId?> MoveUidAsync(
        ImapClient client, IMailFolder source, UniqueId uid, IMailFolder destination, CancellationToken ct)
    {
        if (client.Capabilities.HasFlag(ImapCapabilities.Move))
        {
            return await source.MoveToAsync(uid, destination, ct).ConfigureAwait(false);
        }

        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            throw new MailMoveException("サーバーが MOVE にも UIDPLUS にも対応していないため、移動できません。");
        }

        var copied = await source.CopyToAsync(uid, destination, ct).ConfigureAwait(false);
        await source.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct)
            .ConfigureAwait(false);
        await source.ExpungeAsync([uid], ct).ConfigureAwait(false);
        return copied;
    }

    private void EnsureSameAccount(string accountId)
    {
        // アカウントをまたぐ移動は仕組みとして存在させない
        if (accountId != AccountId)
        {
            throw new InvalidOperationException($"{AccountId} の移動口で {accountId} のメールは扱えません。");
        }
    }
}
