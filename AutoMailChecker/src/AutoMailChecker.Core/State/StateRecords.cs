namespace AutoMailChecker.Core.State;

public enum QueueState
{
    Waiting,
    Processing,
    Failed,
}

/// <summary>処理待ちの1通（queue_item）。TrialRunId が null なら新着。</summary>
public sealed record QueueItem(
    long Id,
    MailRef Mail,
    long? TrialRunId,
    QueueState State,
    int Attempts,
    string? LastError,
    DateTimeOffset EnqueuedAt);

/// <summary>監視フォルダーの状態（folder_state）。</summary>
/// <param name="BaselineUid">初回起動時の最大 UID。これ以前は自動処理しない</param>
/// <param name="LastProcessedUid">キューに登録した最大の UID</param>
public sealed record FolderState(
    string AccountId,
    string Folder,
    uint UidValidity,
    uint BaselineUid,
    DateTimeOffset BaselineAt,
    uint LastProcessedUid,
    DateTimeOffset UpdatedAt);

/// <summary>判定時点の該当項目の控え（judgement_item）。設定で項目を消しても過去の履歴が読めるようにする。</summary>
public sealed record JudgementItemRecord(ItemId ItemId, string Label, int Priority);

/// <summary>判定履歴の1件（judgement）。本文は保存しない。</summary>
public sealed record JudgementRecord
{
    public long Id { get; init; }
    public required MailRef Mail { get; init; }
    public string? MessageId { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? From { get; init; }

    /// <summary>先頭 200 文字だけ保存する。</summary>
    public string? Subject { get; init; }

    public bool IsAdvertisement { get; init; }

    /// <summary>判定失敗時は null。</summary>
    public Confidence? Confidence { get; init; }

    public string? Summary { get; init; }
    public string? Reason { get; init; }
    public required Outcome Outcome { get; init; }
    public string? TargetItemId { get; init; }
    public string? TargetFolder { get; init; }
    public long? TrialRunId { get; init; }
    public string? ModelName { get; init; }
    public string? PromptVersion { get; init; }
    public long? DurationMs { get; init; }
    public required DateTimeOffset JudgedAt { get; init; }

    /// <summary>true なら保持期間を過ぎても消さない（誤判定が記録されたとき）。</summary>
    public bool Keep { get; init; }

    public IReadOnlyList<JudgementItemRecord> Items { get; init; } = [];
}

/// <summary>移動記録（move_record）。1つの判定に移動は最大1回。</summary>
public sealed record MoveRecord
{
    public long Id { get; init; }
    public long JudgementId { get; init; }
    public required MovedMailRef Moved { get; init; }
    public required DateTimeOffset MovedAt { get; init; }
    public DateTimeOffset? UndoneAt { get; init; }
    public UndoResult? UndoResult { get; init; }
}

public enum CorrectionSource
{
    /// <summary>元に戻したとき</summary>
    Undo,
    /// <summary>画面から記録</summary>
    Manual,
}

public enum TrialMode
{
    /// <summary>結果の表示のみ</summary>
    Preview,
    /// <summary>実際に移動する</summary>
    Move,
}

public enum TrialRunStatus
{
    Running,
    Done,
    Cancelled,
}

/// <summary>お試し仕分けの回（trial_run）。</summary>
public sealed record TrialRun
{
    public long Id { get; init; }
    public required DateOnly PeriodFrom { get; init; }
    public required DateOnly PeriodTo { get; init; }
    public required IReadOnlyList<string> Accounts { get; init; }
    public required IReadOnlyList<string> Folders { get; init; }
    public required TrialMode Mode { get; init; }
    public required int MaxMessages { get; init; }
    public TrialRunStatus Status { get; init; } = TrialRunStatus.Running;
    public int Total { get; init; }
    public int Processed { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? RolledBackAt { get; init; }
}

/// <summary>判定履歴の絞り込み。新しい順に Offset から Limit 件を返す。</summary>
public sealed record JudgementQuery
{
    public DateTimeOffset? ReceivedFrom { get; init; }
    public DateTimeOffset? ReceivedTo { get; init; }
    public string? AccountId { get; init; }
    public IReadOnlyCollection<Outcome>? Outcomes { get; init; }
    public long? TrialRunId { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 200;
}

public sealed record PurgeResult(int Judgements, int ProcessedMessages);
