namespace AutoMailChecker.Core;

/// <summary>お試し仕分けの対象選び。上限を超えたら、受信日時順に並べて期間内から等間隔に間引く。</summary>
public static class TrialSampler
{
    public static IReadOnlyList<FoundMail> Select(IReadOnlyList<FoundMail> found, int maxMessages)
    {
        var ordered = found
            .OrderBy(m => m.Received)
            .ThenBy(m => m.Ref.AccountId, StringComparer.Ordinal)
            .ThenBy(m => m.Ref.Uid)
            .ToList();
        if (maxMessages <= 0)
        {
            return [];
        }

        if (ordered.Count <= maxMessages)
        {
            return ordered;
        }

        if (maxMessages == 1)
        {
            return [ordered[ordered.Count / 2]];
        }

        // 最初と最後を含め、等間隔に選ぶ
        var step = (ordered.Count - 1) / (double)(maxMessages - 1);
        return Enumerable.Range(0, maxMessages)
            .Select(i => ordered[(int)Math.Round(i * step)])
            .ToList();
    }
}
