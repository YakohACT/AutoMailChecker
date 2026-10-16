using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 処理待ちから1通ずつ取り出し、除外判定 → 本文取得 → AI 判定 → 振り分け先の決定 → 記録 を進める（シーケンス図 SQ-1）。
/// 開発ステップ3（AI 判定）の段階なので、移動はまだ行わず、移動予定先（outcome は planned など）を判定履歴に残す。
/// llama-server が準備できていない間は取り出さない（処理待ちは溜まり、準備ができたら古い順に判定する）。
/// 取り出した1通は、終了の指示が出ても猶予の間は最後まで処理する。
/// </summary>
public sealed class MailProcessingService(
    IStateStore store, IMailAccountRegistry accounts, IMailClassifier classifier, ILlmRuntime runtime, ActionPolicy policy,
    IConfigProvider config, QueueSignal queueSignal, AppStatusService status, TimeProvider time,
    ILogger<MailProcessingService> logger) : BackgroundService
{
    /// <summary>想定外のエラーで処理できなかったときの試行回数の上限。超えたら failed にして諦める。</summary>
    internal const int MaxAttempts = 5;

    /// <summary>サーバーに繋がらないアカウントの分を後回しにする時間。</summary>
    internal static readonly TimeSpan AccountCooldown = TimeSpan.FromMinutes(1);

    /// <summary>llama-server に繋がらなかったとき、次に判定を試すまでの時間。</summary>
    internal static readonly TimeSpan LlmCooldown = TimeSpan.FromSeconds(30);

    /// <summary>終了の指示が出てから、処理中の1通を待つ時間。過ぎたら処理待ちに戻し、次の起動で続きから処理する。</summary>
    internal static readonly TimeSpan ItemGracePeriod = TimeSpan.FromMinutes(2);

    /// <summary>処理待ちが空のとき、知らせがなくても確認し直す間隔。</summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ErrorWait = TimeSpan.FromSeconds(5);

    /// <summary>ログに残す件名の文字数（本文は書かない）。</summary>
    private const int SubjectLogLength = 20;

    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = [];
    private DateTimeOffset _llmCooldownUntil = DateTimeOffset.MinValue;
    private IReadOnlyList<string>? _filterSource;
    private SenderFilter _filter = new([]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool processed;
                try
                {
                    processed = await ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "処理待ちの取り出しに失敗しました。");
                    await Task.Delay(ErrorWait, time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!processed)
                {
                    await queueSignal.WaitAsync(IdleWait, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 終了時
        }
    }

    /// <summary>1通を処理する。取り出せる分がない（AI の準備中、処理待ちが空、後回し中の分だけ）なら false。</summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        QueueItem? item = null;
        if (runtime.Running is not null && time.GetUtcNow() >= _llmCooldownUntil)
        {
            item = await store.Queue.TakeNextAsync(CoolingDownAccounts(), ct).ConfigureAwait(false);
        }

        if (item is not null)
        {
            // 終了の指示が出ても、猶予の間は処理中の1通を続ける
            using var itemCts = new CancellationTokenSource();
            using var registration = ct.Register(() => itemCts.CancelAfter(ItemGracePeriod));
            await ProcessAsync(item, itemCts.Token).ConfigureAwait(false);
        }

        status.SetPendingCount(await store.Queue.CountPendingAsync(ct).ConfigureAwait(false));
        return item is not null;
    }

    private async Task ProcessAsync(QueueItem item, CancellationToken ct)
    {
        var mail = item.Mail;
        var settings = config.Current;
        try
        {
            if (!accounts.TryGetReader(mail.AccountId, out var reader))
            {
                logger.LogWarning("{Account} UID {Uid}: 設定にないアカウントなので処理待ちから外します。", mail.AccountId, mail.Uid);
                await store.Queue.RemoveAsync(item, ct).ConfigureAwait(false);
                return;
            }

            // 1. ヘッダーだけを取り、除外リストと照合する。一致したら本文は取らず、AI にも渡さず、判定履歴にも残さない
            var header = await reader.FetchHeaderAsync(mail, ct).ConfigureAwait(false);
            if (GetFilter(settings).IsExcluded(header.From))
            {
                await store.Queue.CompleteExcludedAsync(item, header.MessageId, ct).ConfigureAwait(false);
                logger.LogInformation("{Account} UID {Uid}: 除外リストに一致しました。", mail.AccountId, mail.Uid);
                return;
            }

            // 2. 処理済みと同じメール（UIDVALIDITY 変更後の再走査、受信トレイへ戻したメールなど）は二度処理しない
            if (item.TrialRunId is null && header.MessageId.Length > 0
                && await store.ProcessedMessages.ContainsAsync(mail.AccountId, header.MessageId, ct).ConfigureAwait(false))
            {
                await store.Queue.RemoveAsync(item, ct).ConfigureAwait(false);
                logger.LogInformation("{Account} UID {Uid}: 処理済みのメールと同じなので外しました。", mail.AccountId, mail.Uid);
                return;
            }

            // 3. 本文を既読にしない方法で取り、AI で判定する
            var snapshot = await reader.FetchSnapshotAsync(mail, settings.Llm.MaxBodyChars, ct).ConfigureAwait(false);
            var startedAt = time.GetTimestamp();
            Judgement judgement;
            try
            {
                judgement = await classifier.ClassifyAsync(snapshot, settings.Items, ct).ConfigureAwait(false);
            }
            catch (ClassificationException ex)
            {
                await RecordFailedAsync(item, header, ex.Message, time.GetElapsedTime(startedAt), ct).ConfigureAwait(false);
                return;
            }

            var duration = time.GetElapsedTime(startedAt);

            // 4. 振り分け先を決める。移動は開発ステップ4で足すので、今は移動予定先だけを記録する
            var decision = policy.Decide(judgement, PolicyContext.From(settings, mail.AccountId, previewOnly: true));
            await store.Judgements.RecordAsync(BuildRecord(item, header, judgement, decision, duration, settings), move: null,
                completed: item, ct).ConfigureAwait(false);
            _cooldownUntil.Remove(mail.AccountId);
            logger.LogInformation("{Account} UID {Uid}: 判定しました「{Subject}」→ {Outcome} {Target}（確信度 {Confidence}、{Seconds:0.0} 秒）",
                mail.AccountId, mail.Uid, Head(header.Subject), decision.Outcome.ToDbValue(), decision.Target?.Folder ?? "",
                judgement.Confidence.ToDbValue(), duration.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 終了の猶予を過ぎた。処理待ちに戻し、次の起動で続きから処理する
            logger.LogWarning("{Account} UID {Uid}: 終了のため処理を中断しました。", mail.AccountId, mail.Uid);
            await store.Queue.ReleaseAsync(item.Id, "終了のため中断しました", int.MaxValue, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MailNotFoundException ex)
        {
            logger.LogInformation("{Message} 処理待ちから外します。", ex.Message);
            await store.Queue.RemoveAsync(item, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MailServerException ex)
        {
            // 繋がらないアカウントの分はしばらく後回しにし、ほかのアカウントを先に処理する。試行回数の上限には数えない
            _cooldownUntil[mail.AccountId] = time.GetUtcNow() + AccountCooldown;
            logger.LogWarning("{Account} UID {Uid}: サーバーに繋がらないので後回しにします: {Message}", mail.AccountId, mail.Uid, ex.Message);
            await store.Queue.ReleaseAsync(item.Id, ex.Message, int.MaxValue, CancellationToken.None).ConfigureAwait(false);
        }
        catch (LlmUnavailableException ex)
        {
            // AI のせいではないので判定失敗にはせず、処理待ちに戻して後で判定する
            _llmCooldownUntil = time.GetUtcNow() + LlmCooldown;
            logger.LogWarning("{Account} UID {Uid}: llama-server に繋がらないので後で判定します: {Message}", mail.AccountId, mail.Uid, ex.Message);
            await store.Queue.ReleaseAsync(item.Id, ex.Message, int.MaxValue, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Account} UID {Uid}: 処理に失敗しました（{Attempt}/{Max} 回目）。",
                mail.AccountId, mail.Uid, item.Attempts + 1, MaxAttempts);
            await store.Queue.ReleaseAsync(item.Id, ex.Message, MaxAttempts, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private JudgementRecord BuildRecord(
        QueueItem item, MailHeader header, Judgement judgement, MoveDecision decision, TimeSpan duration, AppConfig settings)
    {
        // 判定時点の表示名と優先度を控えとして残す（設定で項目を消しても過去の履歴が読めるように）
        var definitions = new Dictionary<ItemId, ItemDefinition>();
        foreach (var definition in settings.Items)
        {
            definitions.TryAdd(definition.Id, definition);
        }

        return NewRecord(item, header, duration) with
        {
            IsAdvertisement = judgement.IsAdvertisement,
            Confidence = judgement.Confidence,
            Summary = judgement.Summary,
            Reason = judgement.Reason,
            Outcome = decision.Outcome,
            TargetItemId = decision.Target is ItemTarget target ? target.Item.Value : null,
            TargetFolder = decision.Target?.Folder,
            Items = judgement.MatchedItems
                .Where(definitions.ContainsKey)
                .Select(id => new JudgementItemRecord(id, definitions[id].Label, definitions[id].Priority))
                .ToList(),
        };
    }

    /// <summary>判定失敗。メールは受信トレイに残り、自動では再判定しない。</summary>
    private async Task RecordFailedAsync(QueueItem item, MailHeader header, string problem, TimeSpan duration, CancellationToken ct)
    {
        var record = NewRecord(item, header, duration) with { Outcome = Outcome.Failed, Reason = problem };
        await store.Judgements.RecordAsync(record, move: null, completed: item, ct).ConfigureAwait(false);
        logger.LogWarning("{Account} UID {Uid}: 判定に失敗しました「{Subject}」: {Problem}",
            item.Mail.AccountId, item.Mail.Uid, Head(header.Subject), problem);
    }

    private JudgementRecord NewRecord(QueueItem item, MailHeader header, TimeSpan duration) => new()
    {
        Mail = item.Mail,
        MessageId = header.MessageId.Length > 0 ? header.MessageId : null,
        ReceivedAt = header.Received,
        From = header.From,
        Subject = header.Subject,
        Outcome = Outcome.Failed,
        TrialRunId = item.TrialRunId,
        ModelName = runtime.ModelName,
        PromptVersion = classifier.PromptVersion,
        DurationMs = (long)duration.TotalMilliseconds,
        JudgedAt = time.GetUtcNow(),
    };

    private List<string> CoolingDownAccounts()
    {
        var now = time.GetUtcNow();
        return _cooldownUntil.Where(p => p.Value > now).Select(p => p.Key).ToList();
    }

    /// <summary>除外リストは設定が変わったら作り直す（再起動なしで反映）。</summary>
    private SenderFilter GetFilter(AppConfig settings)
    {
        var senders = settings.IgnoreSenders;
        if (!ReferenceEquals(senders, _filterSource))
        {
            _filter = new SenderFilter(senders);
            _filterSource = senders;
        }

        return _filter;
    }

    private static string Head(string subject) =>
        subject.Length <= SubjectLogLength ? subject : subject[..SubjectLogLength] + "…";
}
