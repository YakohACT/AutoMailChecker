using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;

namespace AutoMailChecker.Services;

/// <summary>
/// 項目タブの「試し判定」。判定履歴の1通を保存前の設定で判定し直し、結果だけを返す（移動も記録もしない）。
/// 本文はいまメールがある場所から BODY.PEEK で取り直す（移動済みなら移動先、戻したなら受信トレイ）。
/// </summary>
public sealed class RejudgeService(
    IStateStore store, IMailAccountRegistry accounts, IMailClassifier classifier, ILlmRuntime runtime,
    ActionPolicy policy, TimeProvider time) : IRejudgeService
{
    public bool IsAvailable => runtime.Running is not null;

    public async Task<RejudgeResult> RejudgeAsync(long judgementId, AppConfig draft, CancellationToken ct)
    {
        var record = await store.Judgements.GetAsync(judgementId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("判定履歴が見つかりません。");
        if (!accounts.TryGetReader(record.Mail.AccountId, out var reader))
        {
            throw new ArgumentException($"アカウント「{record.Mail.AccountId}」が設定にありません。");
        }

        var snapshot = await FetchCurrentAsync(reader, record, draft.Llm.MaxBodyChars, ct).ConfigureAwait(false);
        var startedAt = time.GetTimestamp();
        var judgement = await classifier.ClassifyAsync(snapshot, draft.Items, ct).ConfigureAwait(false);
        var duration = time.GetElapsedTime(startedAt);

        var decision = policy.Decide(judgement, PolicyContext.From(draft, record.Mail.AccountId));
        var labels = judgement.MatchedItems
            .Select(id => draft.Items.FirstOrDefault(i => i.Id == id)?.Label ?? id.Value)
            .ToList();
        return new RejudgeResult(judgement, labels, Describe(decision), duration);
    }

    private static string Describe(MoveDecision decision) => decision.Target is { } target
        ? target.Folder
        : decision.Outcome switch
        {
            Outcome.KeptAd => "受信トレイに残す（広告）",
            Outcome.KeptLowConfidence => "受信トレイに残す（確信度が低い）",
            Outcome.KeptNoMatch => "受信トレイに残す（該当なし）",
            Outcome.KeptNoFolder => "受信トレイに残す（振り分け先なし）",
            _ => "受信トレイに残す",
        };

    /// <summary>いまメールがある場所から本文を取る。UID で見つからなければ Message-ID で探す。</summary>
    private static async Task<MailSnapshot> FetchCurrentAsync(IMailReader reader, JudgementRecord record, int maxBodyChars, CancellationToken ct)
    {
        var candidates = new List<MailRef>();
        var folders = new List<string>();
        if (record.Move is { UndoneAt: null } move)
        {
            if (move.Moved.DestUid is { } uid && move.Moved.DestUidValidity is { } validity)
            {
                candidates.Add(new MailRef(record.Mail.AccountId, move.Moved.ToFolder, validity, uid));
            }

            folders.Add(move.Moved.ToFolder);
        }
        else
        {
            candidates.Add(record.Mail);
        }

        folders.Add(record.Mail.Folder);

        foreach (var candidate in candidates)
        {
            try
            {
                return await reader.FetchSnapshotAsync(candidate, maxBodyChars, ct).ConfigureAwait(false);
            }
            catch (MailNotFoundException)
            {
            }
        }

        if (!string.IsNullOrEmpty(record.MessageId))
        {
            foreach (var folder in folders.Distinct(StringComparer.Ordinal))
            {
                if (await reader.FindByMessageIdAsync(folder, record.MessageId, ct).ConfigureAwait(false) is { } found)
                {
                    return await reader.FetchSnapshotAsync(found, maxBodyChars, ct).ConfigureAwait(false);
                }
            }
        }

        throw new MailNotFoundException(record.Mail, "メールが見つかりません（移動または削除された可能性があります）。");
    }
}
