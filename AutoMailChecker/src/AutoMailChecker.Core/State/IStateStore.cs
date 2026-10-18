namespace AutoMailChecker.Core.State;

/// <summary>
/// アプリの状態（%LOCALAPPDATA%\AutoMailChecker\state.db）。E-R 図の表ごとにリポジトリを分ける。
/// 複数の表にまたがる書き込みは、各メソッドの中で1つのトランザクションにまとめる。
/// </summary>
public interface IStateStore
{
    /// <summary>スキーマを最新の版に移行し、処理中のまま残った行を処理待ちに戻す。起動時に1回呼ぶ。</summary>
    Task InitializeAsync(CancellationToken ct);

    IQueueRepository Queue { get; }
    IFolderStateRepository Folders { get; }
    IProcessedMessageRepository ProcessedMessages { get; }
    IJudgementRepository Judgements { get; }
    ITrialRunRepository TrialRuns { get; }

    /// <summary>保持期間を過ぎた判定履歴（keep が 0 のもの）と処理済みメールの記録を消す。</summary>
    Task<PurgeResult> PurgeExpiredAsync(int retentionDays, CancellationToken ct);
}

/// <summary>処理待ち（queue_item）。新着はお試しより常に先に処理する。</summary>
public interface IQueueRepository
{
    /// <summary>同じメールがすでにあれば積まずに false を返す。</summary>
    Task<bool> EnqueueAsync(MailRef mail, long? trialRunId, CancellationToken ct);

    /// <summary>次の1件を処理中にして返す。なければ null。</summary>
    Task<QueueItem?> TakeNextAsync(CancellationToken ct) => TakeNextAsync([], ct);

    /// <summary>
    /// 次の1件を処理中にして返す。skipAccounts のアカウント（接続できず待っている間など）の分は取らない。
    /// </summary>
    Task<QueueItem?> TakeNextAsync(IReadOnlyCollection<string> skipAccounts, CancellationToken ct);

    /// <summary>
    /// 記録せずにキューから消す（サーバーにもうない、処理済みと同じメールだった、など）。
    /// お試しの対象なら、その回の処理済み件数は進める。
    /// </summary>
    Task RemoveAsync(QueueItem item, CancellationToken ct);

    /// <summary>処理できなかった1件を処理待ちに戻す。試行回数が上限に達したら failed にする。</summary>
    Task ReleaseAsync(long id, string error, int maxAttempts, CancellationToken ct);

    /// <summary>除外リストに一致した1件を終える。新着なら処理済みとして記録し、キューから消す。</summary>
    Task CompleteExcludedAsync(QueueItem item, string? messageId, CancellationToken ct);

    /// <summary>未処理（処理待ちと処理中）の件数。トレイのツールチップに出す。</summary>
    Task<int> CountPendingAsync(CancellationToken ct);

    /// <summary>そのお試しの回で、まだ処理していない件数。0 になったら回を終える。</summary>
    Task<int> CountPendingForTrialAsync(long trialRunId, CancellationToken ct);

    /// <summary>移動する直前に、判定結果と移動先の控えを残す。</summary>
    Task SavePendingMoveAsync(long id, string pendingMove, CancellationToken ct);
}

/// <summary>監視フォルダーの状態（folder_state）。</summary>
public interface IFolderStateRepository
{
    Task<FolderState?> GetAsync(string accountId, string folder, CancellationToken ct);

    Task UpsertAsync(FolderState state, CancellationToken ct);
}

/// <summary>処理済みメール（processed_message）。UIDVALIDITY が変わったときの重複防止に使う。</summary>
public interface IProcessedMessageRepository
{
    Task<bool> ContainsAsync(string accountId, string messageId, CancellationToken ct);

    /// <summary>処理済みとして記録する。受信トレイへ戻したメールが、新着として再び仕分けられないようにする。</summary>
    Task MarkProcessedAsync(string accountId, string messageId, CancellationToken ct);
}

/// <summary>判定履歴（judgement とその子の表）。</summary>
public interface IJudgementRepository
{
    /// <summary>
    /// 判定1件分（judgement、judgement_item、move_record、processed_message、queue_item の削除）を
    /// 1つのトランザクションで書く。
    /// </summary>
    Task<long> RecordAsync(JudgementRecord judgement, MoveRecord? move, QueueItem? completed, CancellationToken ct);

    Task<JudgementRecord?> GetAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<JudgementRecord>> QueryAsync(JudgementQuery query, CancellationToken ct);

    /// <summary>絞り込んだ判定履歴全体の件数（ページに関係なく数える）。</summary>
    Task<HistorySummary> SummarizeAsync(JudgementQuery query, CancellationToken ct);

    /// <summary>直近の判定にかかった時間の平均。件数が minSamples に満たなければ null。お試しの見込み時間に使う。</summary>
    Task<TimeSpan?> AverageDurationAsync(int sampleSize, int minSamples, CancellationToken ct);

    Task<MoveRecord?> GetMoveRecordAsync(long judgementId, CancellationToken ct);

    /// <summary>
    /// 「元に戻す」の結果を記録する。戻した（restored）か、すでに別の場所にあった（not_found）なら戻した日時も入れる。
    /// 失敗（failed）なら日時は入れず、もう一度試せるようにする。
    /// </summary>
    Task MarkUndoneAsync(long moveRecordId, UndoResult result, CancellationToken ct);

    /// <summary>誤判定を記録し、その判定を保持期間後も残す。correctItemId が null なら「どの項目でもない」。</summary>
    Task AddCorrectionAsync(long judgementId, string? correctItemId, CorrectionSource source, CancellationToken ct);
}

/// <summary>お試し仕分けの回（trial_run）。</summary>
public interface ITrialRunRepository
{
    /// <summary>回を作り、対象を trial_run_id 付きでキューに積む。Total は実際に積んだ件数になる。</summary>
    Task<TrialRun> CreateAsync(TrialRun run, IReadOnlyList<MailRef> targets, CancellationToken ct);

    Task<TrialRun?> GetAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<TrialRun>> ListAsync(CancellationToken ct);

    /// <summary>キューに残った分を消して終える。</summary>
    Task FinishAsync(long id, TrialRunStatus status, CancellationToken ct);

    Task MarkRolledBackAsync(long id, CancellationToken ct);
}
