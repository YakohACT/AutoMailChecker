using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Core;

/// <summary>ActionPolicy が判断に使う設定。1通ごとに作る。</summary>
/// <param name="AccountId">判定したメールのアカウント。振り分け先はこのアカウントのフォルダーからだけ選ぶ</param>
/// <param name="MoveAllowed">false なら移動予定先だけ決める（お試しの「結果の表示のみ」、allowMove が false のアカウント）</param>
/// <param name="UnavailableFolders">サーバーに見つからなかったフォルダー。ここにあるフォルダーへは移動しない</param>
public sealed record PolicyContext(
    string AccountId,
    IReadOnlyList<ItemDefinition> Items,
    Confidence MinConfidence,
    bool MoveAllowed,
    IReadOnlySet<string>? UnavailableFolders = null)
{
    public static PolicyContext From(
        AppConfig config, string accountId, bool previewOnly = false, IReadOnlySet<string>? unavailableFolders = null)
    {
        var allowMove = config.FindAccount(accountId)?.AllowMove ?? false;
        return new PolicyContext(
            accountId, config.Items, config.Policy.MinConfidence,
            MoveAllowed: allowMove && !previewOnly, unavailableFolders);
    }

    public bool IsUsable(string folder) => UnavailableFolders is null || !UnavailableFolders.Contains(folder);
}

/// <summary>
/// AI の判定から移動先を決める。確信度 → 優先度の順に見る。入出力はしない。
/// AI が返すのは項目 ID だけで、フォルダーと優先度はここで設定から決める。
/// </summary>
public sealed class ActionPolicy
{
    public MoveDecision Decide(Judgement judgement, PolicyContext context)
    {
        var decision = DecideTarget(judgement, context);
        return decision.Target is not null && !context.MoveAllowed
            ? decision with { Outcome = Outcome.Planned }
            : decision;
    }

    private static MoveDecision DecideTarget(Judgement judgement, PolicyContext context)
    {
        // 1. 確信度がしきい値未満なら、該当項目に関係なく残す
        if (!judgement.Confidence.IsAtLeast(context.MinConfidence))
        {
            return new MoveDecision(null, Outcome.KeptLowConfidence);
        }

        // 2. 有効な該当項目を優先度順に見て、このアカウントに振り分け先がある最初の項目へ
        var matched = context.Items
            .Where(item => item.Enabled && judgement.MatchedItems.Contains(item.Id))
            .OrderBy(item => item.Priority)
            .ToList();
        if (matched.Count == 0)
        {
            return new MoveDecision(null, Outcome.KeptNoMatch);
        }

        foreach (var item in matched)
        {
            var folder = item.FolderFor(context.AccountId);
            if (folder is not null && context.IsUsable(folder))
            {
                return new MoveDecision(new ItemTarget(item.Id, folder), Outcome.Moved);
            }
        }

        return new MoveDecision(null, Outcome.KeptNoFolder);
    }
}
