using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// 処理待ちから1通ずつ取り出し、除外判定 → 本文取得 → AI 判定 → 振り分け先の決定 → 移動 → 記録 を進める（シーケンス SQ-1）。
/// - 移動するのは、新着なら自動仕分け（policy.autoSort）がオンのとき、お試しなら「実際に移動する」の回だけ。
///   それ以外は移動予定先（planned）を記録する。アカウントの allowMove が false なら移動しない
/// - 移動の直前に判定結果と移動先の控えを残し、移動の後・記録の前に落ちても、再起動後に履歴を補う
/// - 判定保留・完全停止の間と、llama-server が準備できていない間は取り出さない（処理待ちは溜まり、再開したら古い順に判定する）
/// - llama-server の切り替え中は取り出さない（ProcessingGate）
/// - 取り出した1通は、終了の指示が出ても猶予の間は最後まで処理する
/// </summary>
public sealed class MailProcessingService(
    IStateStore store, IMailAccountRegistry accounts, IMailClassifier classifier, ILlmRuntime runtime, ActionPolicy policy,
    IRunModeSource runModes, ProcessingGate gate, IConfigProvider config, QueueSignal queueSignal, AppStatusService status,
    TimeProvider time, ILogger<MailProcessingService> logger) : BackgroundService
{
    /// <summary>移動の失敗をトレイに出すときの Source。</summary>
    public const string MoveErrorSource = "振り分け";

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
    private readonly Dictionary<long, TrialRun?> _trialRuns = [];
    private readonly HashSet<(string Account, string Folder)> _missingFolders = [];
    private AppConfig? _missingFoldersConfig;
    private DateTimeOffset _llmCooldownUntil = DateTimeOffset.MinValue;
    private IReadOnlyList<string>? _filterSource;
    private SenderFilter _filter = new([]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ShowRunningTrialAsync(stoppingToken).ConfigureAwait(false);
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

    /// <summary>
    /// 1通を処理する。取り出せる分がない（停止中、AI の準備中・切り替え中、処理待ちが空、後回し中の分だけ）なら false。
    /// </summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        QueueItem? item = null;
        if (CanJudge() && gate.TryEnter())
        {
            try
            {
                item = await store.Queue.TakeNextAsync(CoolingDownAccounts(), ct).ConfigureAwait(false);
                if (item is not null)
                {
                    // 終了の指示が出ても、猶予の間は処理中の1通を続ける
                    using (var itemCts = new CancellationTokenSource())
                    using (ct.Register(() => itemCts.CancelAfter(ItemGracePeriod)))
                    {
                        await ProcessAsync(item, itemCts.Token).ConfigureAwait(false);
                    }

                    if (item.TrialRunId is { } trialRunId)
                    {
                        await UpdateTrialAsync(trialRunId, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                gate.Exit(); // 完全停止などの切り替えを待っている側に、処理中の1通が終わったことを知らせる
            }
        }

        status.SetPendingCount(await store.Queue.CountPendingAsync(ct).ConfigureAwait(false));
        return item is not null;
    }

    /// <summary>判定してよいか。稼働中か低負荷で継続のときで、llama-server が準備できていること。</summary>
    private bool CanJudge() =>
        runModes.Current.Mode is RunMode.Running or RunMode.LowLoad
        && runtime.Running is not null
        && time.GetUtcNow() >= _llmCooldownUntil;

    private async Task ProcessAsync(QueueItem item, CancellationToken ct)
    {
        var mail = item.Mail;
        var settings = config.Current;
        try
        {
            if (!accounts.TryGetReader(mail.AccountId, out var reader) || !accounts.TryGetMover(mail.AccountId, out var mover))
            {
                logger.LogWarning("{Account} UID {Uid}: 設定にないアカウントなので処理待ちから外します。", mail.AccountId, mail.Uid);
                await store.Queue.RemoveAsync(item, ct).ConfigureAwait(false);
                return;
            }

            // 前回、移動の直前で止まっていたら、判定し直さずに続きから処理する
            if (item.PendingMove is { } pending)
            {
                await ResumePendingMoveAsync(item, reader, mover, PendingMove.FromJson(pending), ct).ConfigureAwait(false);
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

            // 4. 振り分け先を決める（確信度 → 広告 → 優先度）。移動しないときは移動予定先だけを記録する
            var trial = await GetTrialRunAsync(item.TrialRunId, ct).ConfigureAwait(false);
            var context = PolicyContext.From(settings, mail.AccountId,
                previewOnly: !MoveEnabled(item, trial, settings), MissingFolders(settings, mail.AccountId));
            var decision = policy.Decide(judgement, context);
            var record = BuildRecord(item, header, judgement, decision, duration, settings);

            if (!decision.ShouldMove || decision.Target is null)
            {
                await store.Judgements.RecordAsync(record, move: null, completed: item, ct).ConfigureAwait(false);
                LogJudged(item, header, judgement, decision.Outcome, decision.Target?.Folder, duration);
                return;
            }

            // 5. 移動する。直前に控えを残しておく
            var pendingMove = PendingMove.Create(record, decision.Target);
            await store.Queue.SavePendingMoveAsync(item.Id, pendingMove.ToJson(), ct).ConfigureAwait(false);
            await MoveAndRecordAsync(item, mover, pendingMove, ct).ConfigureAwait(false);
            LogJudged(item, header, judgement, decision.Outcome, decision.Target.Folder, duration);
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

    /// <summary>移動して、判定1件分を1つのトランザクションで記録する。移動できなければ受信トレイに残し、失敗を明記する。</summary>
    private async Task MoveAndRecordAsync(QueueItem item, IItemFolderMover mover, PendingMove pending, CancellationToken ct)
    {
        var record = pending.Record;
        MovedMailRef moved;
        try
        {
            moved = pending.ItemId is { } itemId
                ? await mover.MoveToItemFolderAsync(item.Mail, new ItemId(itemId), ct).ConfigureAwait(false)
                : await mover.MoveToAdvertisementFolderAsync(item.Mail, ct).ConfigureAwait(false);
        }
        catch (MailFolderNotFoundException ex)
        {
            // 振り分け先がサーバーにない。このフォルダーへの移動を止め（次からは次点の項目へ）、トレイで知らせる
            _missingFolders.Add((item.Mail.AccountId, ex.Folder));
            status.ReportError(MoveErrorSource, $"{ex.Message}その振り分け先への移動を止めています。");
            logger.LogWarning("{Message}", ex.Message);
            await store.Judgements.RecordAsync(
                record with { Outcome = Outcome.KeptNoFolder, TargetItemId = null, TargetFolder = null },
                move: null, completed: item, ct).ConfigureAwait(false);
            return;
        }
        catch (MailMoveException ex)
        {
            status.ReportError(MoveErrorSource, $"{item.Mail.AccountId}: 移動に失敗しました: {ex.Message}");
            logger.LogWarning("{Account} UID {Uid}: 移動に失敗しました: {Message}", item.Mail.AccountId, item.Mail.Uid, ex.Message);
            await store.Judgements.RecordAsync(
                record with { Outcome = Outcome.Failed, Reason = $"移動に失敗しました: {ex.Message}（AI の判定理由: {record.Reason}）" },
                move: null, completed: item, ct).ConfigureAwait(false);
            return;
        }
        catch (MailNotFoundException ex)
        {
            logger.LogInformation("{Message}", ex.Message);
            await store.Judgements.RecordAsync(
                record with { Outcome = Outcome.Failed, Reason = "移動する前に、メールが元のフォルダーからなくなっていました。" },
                move: null, completed: item, ct).ConfigureAwait(false);
            return;
        }

        await store.Judgements.RecordAsync(
            record with { TargetFolder = moved.ToFolder },
            new MoveRecord { Moved = moved, MovedAt = time.GetUtcNow() },
            completed: item, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 移動の直前で止まっていた1通の続き。メールがまだ元のフォルダーにあれば移動し直す。
    /// なければ移動先を Message-ID で探し、見つかれば移動済みとして履歴を補う（二重に移動しない）。
    /// </summary>
    private async Task ResumePendingMoveAsync(
        QueueItem item, IMailReader reader, IItemFolderMover mover, PendingMove pending, CancellationToken ct)
    {
        try
        {
            await reader.FetchHeaderAsync(item.Mail, ct).ConfigureAwait(false);
            logger.LogInformation("{Account} UID {Uid}: 前回の移動が終わっていなかったので、移動し直します。", item.Mail.AccountId, item.Mail.Uid);
            await MoveAndRecordAsync(item, mover, pending, ct).ConfigureAwait(false);
            return;
        }
        catch (MailNotFoundException)
        {
            // 元のフォルダーにない。移動は済んでいる可能性が高い
        }

        var record = pending.Record;
        var destination = string.IsNullOrEmpty(record.MessageId)
            ? null
            : await reader.FindByMessageIdAsync(pending.Folder, record.MessageId, ct).ConfigureAwait(false);
        if (destination is { } found)
        {
            var moved = new MovedMailRef(item.Mail.AccountId, item.Mail.Folder, pending.Folder, found.UidValidity, found.Uid);
            await store.Judgements.RecordAsync(record, new MoveRecord { Moved = moved, MovedAt = time.GetUtcNow() }, item, ct)
                .ConfigureAwait(false);
            logger.LogInformation("{Account} UID {Uid}: 移動済みだったので判定履歴を補いました（{Folder}）。",
                item.Mail.AccountId, item.Mail.Uid, pending.Folder);
            return;
        }

        await store.Judgements.RecordAsync(
            record with
            {
                Outcome = Outcome.Failed,
                Reason = "移動の途中で終了したため、移動先を確認できませんでした（元のフォルダーにも振り分け先にも見つかりません）。",
            },
            move: null, completed: item, ct).ConfigureAwait(false);
        logger.LogWarning("{Account} UID {Uid}: 移動の途中で終了したメールが見つかりませんでした。", item.Mail.AccountId, item.Mail.Uid);
    }

    private static bool MoveEnabled(QueueItem item, TrialRun? trial, AppConfig settings) =>
        item.TrialRunId is null
            ? settings.Policy.AutoSort
            : trial is { Mode: TrialMode.Move } && AppInfo.IsInbox(item.Mail.Folder);

    /// <summary>サーバーに見つからなかった振り分け先。設定が変わったら確かめ直す。</summary>
    private IReadOnlySet<string>? MissingFolders(AppConfig settings, string accountId)
    {
        if (!ReferenceEquals(settings, _missingFoldersConfig))
        {
            if (_missingFolders.Count > 0)
            {
                status.ClearError(MoveErrorSource);
            }

            _missingFolders.Clear();
            _missingFoldersConfig = settings;
        }

        var folders = _missingFolders.Where(m => m.Account == accountId).Select(m => m.Folder).ToHashSet(StringComparer.Ordinal);
        return folders.Count > 0 ? folders : null;
    }

    private async Task<TrialRun?> GetTrialRunAsync(long? trialRunId, CancellationToken ct)
    {
        if (trialRunId is not { } id)
        {
            return null;
        }

        if (!_trialRuns.TryGetValue(id, out var run))
        {
            run = await store.TrialRuns.GetAsync(id, ct).ConfigureAwait(false);
            _trialRuns[id] = run;
        }

        return run;
    }

    /// <summary>お試しの進み具合をトレイに出し、残りがなくなったら回を終える。</summary>
    private async Task UpdateTrialAsync(long trialRunId, CancellationToken ct)
    {
        var run = await store.TrialRuns.GetAsync(trialRunId, ct).ConfigureAwait(false);
        if (run is not { Status: TrialRunStatus.Running })
        {
            return;
        }

        if (await store.Queue.CountPendingForTrialAsync(trialRunId, ct).ConfigureAwait(false) == 0)
        {
            await store.TrialRuns.FinishAsync(trialRunId, TrialRunStatus.Done, ct).ConfigureAwait(false);
            status.SetTrial(null);
            logger.LogInformation("お試し仕分け {Id} が終わりました（{Processed}/{Total} 件）。", trialRunId, run.Processed, run.Total);
        }
        else
        {
            status.SetTrial(new TrialProgress(run.Id, run.Processed, run.Total));
        }
    }

    private async Task ShowRunningTrialAsync(CancellationToken ct)
    {
        var running = (await store.TrialRuns.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => r.Status == TrialRunStatus.Running);
        if (running is not null)
        {
            status.SetTrial(new TrialProgress(running.Id, running.Processed, running.Total));
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

    private void LogJudged(QueueItem item, MailHeader header, Judgement judgement, Outcome outcome, string? folder, TimeSpan duration) =>
        logger.LogInformation("{Account} UID {Uid}: 判定しました「{Subject}」→ {Outcome} {Folder}（確信度 {Confidence}、{Seconds:0.0} 秒）",
            item.Mail.AccountId, item.Mail.Uid, Head(header.Subject), outcome.ToDbValue(), folder ?? "",
            judgement.Confidence.ToDbValue(), duration.TotalSeconds);

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
