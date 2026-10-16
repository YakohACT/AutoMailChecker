using AutoMailChecker.Core;
using MailKit;
using MailKit.Search;

namespace AutoMailChecker.Mail.Watching;

/// <summary>受信日時付きの UID。</summary>
internal readonly record struct ReceivedUid(uint Uid, DateTimeOffset Received);

/// <summary>
/// 監視するフォルダーへの読み取り操作。同期の判断（<see cref="FolderSynchronizer"/>）を MailKit から切り離し、
/// 偽物に差し替えてテストできるようにする。
/// </summary>
internal interface IWatchedFolder
{
    /// <summary>設定に書かれたフォルダー名（folder_state と MailRef のキー）。</summary>
    string Name { get; }

    uint UidValidity { get; }

    /// <summary>いまある最大の UID。空なら 0。</summary>
    Task<uint> GetMaxUidAsync(CancellationToken ct);

    /// <summary>uid より大きい UID。サーバーは「n:*」に最後の1通を含めて返すことがあるので、呼び出し側でも絞る。</summary>
    Task<IReadOnlyList<uint>> SearchUidsAboveAsync(uint uid, CancellationToken ct);

    /// <summary>指定日以降に受信したメールの UID と受信日時。</summary>
    Task<IReadOnlyList<ReceivedUid>> SearchReceivedSinceAsync(DateOnly since, CancellationToken ct);
}

/// <summary>IWatchedFolder の MailKit 実装。フォルダーは呼び出し側で開いておく。</summary>
internal sealed class MailKitWatchedFolder(string name, IMailFolder folder) : IWatchedFolder
{
    private static readonly FetchRequest UidOnly = new(MessageSummaryItems.UniqueId);
    private static readonly FetchRequest UidAndDate = new(MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate);

    public string Name => name;

    public uint UidValidity => folder.UidValidity;

    public async Task<uint> GetMaxUidAsync(CancellationToken ct)
    {
        if (folder.Count == 0)
        {
            return 0;
        }

        // 番号の順と UID の順は一致するので、最後の1通の UID が最大
        var last = await folder.FetchAsync(folder.Count - 1, folder.Count - 1, UidOnly, ct).ConfigureAwait(false);
        return last.Count > 0 ? last[0].UniqueId.Id : 0;
    }

    public async Task<IReadOnlyList<uint>> SearchUidsAboveAsync(uint uid, CancellationToken ct)
    {
        // UID SEARCH UID ＜前回＋1＞:*
        var range = new UniqueIdRange(new UniqueId(uid + 1), UniqueId.MaxValue);
        var found = await folder.SearchAsync(SearchQuery.Uids(range), ct).ConfigureAwait(false);
        return found.Select(u => u.Id).ToList();
    }

    public async Task<IReadOnlyList<ReceivedUid>> SearchReceivedSinceAsync(DateOnly since, CancellationToken ct)
    {
        // UID SEARCH SINCE ＜日付＞ のあと、受信日時（INTERNALDATE）を取る
        var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since.ToDateTime(TimeOnly.MinValue)), ct)
            .ConfigureAwait(false);
        if (uids.Count == 0)
        {
            return [];
        }

        var summaries = await folder.FetchAsync(uids, UidAndDate, ct).ConfigureAwait(false);
        return summaries
            .Select(s => new ReceivedUid(s.UniqueId.Id, s.InternalDate ?? DateTimeOffset.MinValue))
            .ToList();
    }
}
