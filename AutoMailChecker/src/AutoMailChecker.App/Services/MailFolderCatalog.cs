using System.Collections.Concurrent;
using AutoMailChecker.Core;

namespace AutoMailChecker.Services;

/// <summary>
/// 設定画面の振り分け先の候補。アカウントごとに問い合わせた結果を 10 分間使い回す（画面設計 SC-02）。
/// 受信トレイ・迷惑メール・送信済みなどの特別なフォルダーは候補にしない。
/// </summary>
public sealed class MailFolderCatalog(IMailAccountRegistry accounts, TimeProvider time) : IMailFolderCatalog
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTimeOffset LoadedAt, IReadOnlyList<string> Folders)> _cache = new();

    public async Task<IReadOnlyList<string>> GetSortingFoldersAsync(string accountId, bool refresh, CancellationToken ct)
    {
        if (!refresh && _cache.TryGetValue(accountId, out var cached) && time.GetUtcNow() - cached.LoadedAt < CacheDuration)
        {
            return cached.Folders;
        }

        if (!accounts.TryGetReader(accountId, out var reader))
        {
            throw new ArgumentException($"アカウント「{accountId}」は、保存してアプリを再起動するまで使えません。");
        }

        var folders = (await reader.ListFoldersAsync(ct).ConfigureAwait(false))
            .Where(f => f.IsSortingCandidate)
            .Select(f => f.Name)
            .ToList();
        _cache[accountId] = (time.GetUtcNow(), folders);
        return folders;
    }
}
