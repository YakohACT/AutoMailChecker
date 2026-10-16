using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 移動したメールを受信トレイへ戻す（シーケンス SQ-3）。ユーザーが押したときだけ動き、自動処理からは呼ばない。
/// 戻し先は同じアカウントの受信トレイだけで、アカウントをまたぐことはない。
/// </summary>
public sealed class UndoService(IStateStore store, IMailAccountRegistry accounts, ILogger<UndoService> logger) : IUndoService
{
    public async Task<UndoOutcome> UndoAsync(long judgementId, bool asMisjudgement, CancellationToken ct)
    {
        var judgement = await store.Judgements.GetAsync(judgementId, ct).ConfigureAwait(false);
        if (judgement?.Move is not { } move)
        {
            return new UndoOutcome(UndoResult.Failed, "このメールは移動していません。");
        }

        if (move.UndoneAt is not null)
        {
            return new UndoOutcome(move.UndoResult ?? UndoResult.Restored, "すでに受信トレイに戻してあります。");
        }

        if (!accounts.TryGetMover(move.Moved.AccountId, out var mover))
        {
            return new UndoOutcome(UndoResult.Failed, $"アカウント「{move.Moved.AccountId}」が設定にありません。");
        }

        UndoResult result;
        string? problem = null;
        try
        {
            result = await mover.MoveBackToInboxAsync(move.Moved, judgement.MessageId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MailServerException or MailMoveException)
        {
            result = UndoResult.Failed;
            problem = ex.Message;
        }

        await store.Judgements.MarkUndoneAsync(move.Id, result, ct).ConfigureAwait(false);
        if (result == UndoResult.Restored)
        {
            // 受信トレイに戻ったメールには新しい UID が付くので、新着として再び仕分けられないよう処理済みにしておく
            if (!string.IsNullOrEmpty(judgement.MessageId))
            {
                await store.ProcessedMessages.MarkProcessedAsync(move.Moved.AccountId, judgement.MessageId, ct).ConfigureAwait(false);
            }

            if (asMisjudgement)
            {
                await store.Judgements.AddCorrectionAsync(judgementId, correctItemId: null, CorrectionSource.Undo, ct)
                    .ConfigureAwait(false);
            }
        }

        logger.LogInformation("{Account}: 判定 {Id} を受信トレイへ戻しました（{Result}）。", move.Moved.AccountId, judgementId, result.ToDbValue());
        return result switch
        {
            UndoResult.Restored => new UndoOutcome(result, asMisjudgement
                ? "受信トレイに戻し、誤判定として記録しました。本来の項目があれば選んで記録できます。"
                : "受信トレイに戻しました。"),
            UndoResult.NotFound => new UndoOutcome(result,
                "移動先にメールが見つかりませんでした。すでに別の場所へ移されているため、何もしていません。"),
            _ => new UndoOutcome(result, $"戻せませんでした: {problem}"),
        };
    }
}
