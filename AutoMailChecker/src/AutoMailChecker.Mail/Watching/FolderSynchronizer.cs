using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Mail.Watching;

/// <summary>
/// 監視フォルダーの新着を処理待ちに積む（仕様書7章「新着検知」）。
/// - 初回は現在の最大 UID を起点として記録し、それ以前の過去メールは自動では処理しない
/// - 以降は「最後に積んだ UID より大きいもの」を探して積む（再開時の取りこぼしもこれで回収する）
/// - UIDVALIDITY が変わったら、起点より後に届いたものに限り直近3日分を受信日時で再走査する。
///   同じメールの二重処理は、処理時に Message-ID（processed_message）で除く
/// </summary>
internal sealed class FolderSynchronizer(IStateStore store, TimeProvider time, ILogger logger)
{
    public static readonly TimeSpan RescanWindow = TimeSpan.FromDays(3);

    /// <summary>積んだ件数を返す。</summary>
    public async Task<int> SyncAsync(string accountId, IWatchedFolder folder, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var state = await store.Folders.GetAsync(accountId, folder.Name, ct).ConfigureAwait(false);

        if (state is null)
        {
            var baseline = await folder.GetMaxUidAsync(ct).ConfigureAwait(false);
            await store.Folders.UpsertAsync(
                new FolderState(accountId, folder.Name, folder.UidValidity, baseline, now, baseline, now), ct).ConfigureAwait(false);
            logger.LogInformation("{Account} {Folder}: 起点 UID {Baseline} を記録しました。これ以前のメールは自動では処理しません。",
                accountId, folder.Name, baseline);
            return 0;
        }

        if (state.UidValidity != folder.UidValidity)
        {
            return await RescanAsync(accountId, folder, state, now, ct).ConfigureAwait(false);
        }

        var uids = (await folder.SearchUidsAboveAsync(state.LastProcessedUid, ct).ConfigureAwait(false))
            .Where(uid => uid > state.LastProcessedUid)
            .Distinct()
            .Order()
            .ToList();
        if (uids.Count == 0)
        {
            return 0;
        }

        // 先に積んでから位置を進める。間で落ちても、同じメールは一意制約で二重に積まれない
        var added = await EnqueueAsync(accountId, folder, uids, ct).ConfigureAwait(false);
        await store.Folders.UpsertAsync(state with { LastProcessedUid = uids[^1], UpdatedAt = now }, ct).ConfigureAwait(false);
        return added;
    }

    private async Task<int> RescanAsync(
        string accountId, IWatchedFolder folder, FolderState state, DateTimeOffset now, CancellationToken ct)
    {
        logger.LogWarning("{Account} {Folder}: UIDVALIDITY が {Old} から {New} に変わりました。直近3日分を再走査します。",
            accountId, folder.Name, state.UidValidity, folder.UidValidity);

        // 先に最大 UID を取る。再走査の間に届いたメールは、次回の通常の検索で拾う
        var max = await folder.GetMaxUidAsync(ct).ConfigureAwait(false);
        var since = now - RescanWindow > state.BaselineAt ? now - RescanWindow : state.BaselineAt;
        // SEARCH SINCE は日付単位（サーバーの時刻帯）なので1日広めに探し、受信日時で絞る
        var candidates = await folder.SearchReceivedSinceAsync(DateOnly.FromDateTime(since.UtcDateTime).AddDays(-1), ct)
            .ConfigureAwait(false);
        var uids = candidates
            .Where(c => c.Received >= since)
            .Select(c => c.Uid)
            .Distinct()
            .Order()
            .ToList();

        var added = await EnqueueAsync(accountId, folder, uids, ct).ConfigureAwait(false);
        var last = uids.Count > 0 ? Math.Max(max, uids[^1]) : max;
        await store.Folders.UpsertAsync(
            state with { UidValidity = folder.UidValidity, LastProcessedUid = last, UpdatedAt = now }, ct).ConfigureAwait(false);
        return added;
    }

    private async Task<int> EnqueueAsync(string accountId, IWatchedFolder folder, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        var added = 0;
        foreach (var uid in uids)
        {
            var mail = new MailRef(accountId, folder.Name, folder.UidValidity, uid);
            if (await store.Queue.EnqueueAsync(mail, trialRunId: null, ct).ConfigureAwait(false))
            {
                added++;
            }
        }

        return added;
    }
}
