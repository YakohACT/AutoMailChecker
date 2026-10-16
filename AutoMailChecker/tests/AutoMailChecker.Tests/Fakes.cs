using System.Diagnostics.CodeAnalysis;
using AutoMailChecker.Core;

namespace AutoMailChecker.Tests;

/// <summary>IMAP の代わりに、メモリ上のフォルダーで読み取りと移動を真似る。</summary>
internal sealed class FakeAccounts : IMailAccountRegistry
{
    private readonly Dictionary<string, FakeMailbox> _mailboxes = [];

    public IReadOnlyCollection<string> AccountIds => _mailboxes.Keys;

    public FakeMailbox Mailbox(string accountId)
    {
        if (!_mailboxes.TryGetValue(accountId, out var mailbox))
        {
            _mailboxes[accountId] = mailbox = new FakeMailbox(accountId);
        }

        return mailbox;
    }

    public bool TryGetReader(string accountId, [NotNullWhen(true)] out IMailReader? reader)
    {
        reader = _mailboxes.GetValueOrDefault(accountId);
        return reader is not null;
    }

    public bool TryGetMover(string accountId, [NotNullWhen(true)] out IItemFolderMover? mover)
    {
        mover = _mailboxes.GetValueOrDefault(accountId);
        return mover is not null;
    }
}

/// <summary>1アカウント分のフォルダーとメール。振り分け先は items / adFolder で決める。</summary>
internal sealed class FakeMailbox(string accountId) : IMailReader, IItemFolderMover
{
    private uint _nextUid = 1000;

    /// <summary>(フォルダー, UID) → ヘッダー。</summary>
    public Dictionary<(string Folder, uint Uid), MailHeader> Mails { get; } = [];

    /// <summary>サーバーにあるフォルダー。ここにない振り分け先へは移動できない。</summary>
    public HashSet<string> Folders { get; } = ["INBOX", "AI/重要", "AI/予約関連", "AI/決済履歴", "AI/給与明細", "AI/広告"];

    public Dictionary<string, string> ItemFolders { get; } = new()
    {
        ["important"] = "AI/重要", ["reservation"] = "AI/予約関連", ["payment"] = "AI/決済履歴", ["payslip"] = "AI/給与明細",
    };

    public Exception? ReadFailure { get; set; }
    public Exception? MoveFailure { get; set; }
    public bool ReturnCopyUid { get; set; } = true;
    public List<(MailRef Mail, string To)> Moves { get; } = [];
    public List<FoundMail> SearchResults { get; } = [];

    public string AccountId => accountId;

    public MailRef Add(uint uid, string from, string subject, string messageId, DateTimeOffset received, string folder = "INBOX")
    {
        var mail = new MailRef(accountId, folder, 777, uid);
        Mails[(folder, uid)] = new MailHeader(mail, messageId, from, subject, received);
        return mail;
    }

    public Task<MailHeader> FetchHeaderAsync(MailRef mail, CancellationToken ct) => Task.FromResult(Find(mail));

    public Task<MailSnapshot> FetchSnapshotAsync(MailRef mail, int maxBodyChars, CancellationToken ct)
    {
        var header = Find(mail);
        return Task.FromResult(new MailSnapshot(header, $"本文 {header.MessageId.Trim('<', '>')}", []));
    }

    public Task<IReadOnlyList<FoundMail>> SearchByDateAsync(string folder, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FoundMail>>(SearchResults
            .Where(f => f.Ref.Folder == folder)
            .Where(f => DateOnly.FromDateTime(f.Received.UtcDateTime) >= from && DateOnly.FromDateTime(f.Received.UtcDateTime) <= to)
            .ToList());

    public Task<MailRef?> FindByMessageIdAsync(string folder, string messageId, CancellationToken ct) =>
        Task.FromResult<MailRef?>(Mails.Values.FirstOrDefault(h => h.Ref.Folder == folder && h.MessageId == messageId)?.Ref);

    public Task<MovedMailRef> MoveToItemFolderAsync(MailRef mail, ItemId item, CancellationToken ct) =>
        Task.FromResult(Move(mail, ItemFolders.GetValueOrDefault(item.Value) ?? throw new MailFolderNotFoundException(accountId, "（未設定）")));

    public Task<MovedMailRef> MoveToAdvertisementFolderAsync(MailRef mail, CancellationToken ct) =>
        Task.FromResult(Move(mail, "AI/広告"));

    public Task<UndoResult> MoveBackToInboxAsync(MovedMailRef moved, string? messageId, CancellationToken ct)
    {
        if (MoveFailure is not null)
        {
            throw MoveFailure;
        }

        var key = Mails.Keys.FirstOrDefault(k => k.Folder == moved.ToFolder
            && (k.Uid == moved.DestUid || (moved.DestUid is null && Mails[k].MessageId == messageId)));
        if (!Mails.TryGetValue(key, out var header))
        {
            return Task.FromResult(UndoResult.NotFound);
        }

        Mails.Remove(key);
        var uid = _nextUid++;
        Mails[("INBOX", uid)] = header with { Ref = header.Ref with { Folder = "INBOX", Uid = uid } };
        return Task.FromResult(UndoResult.Restored);
    }

    private MailHeader Find(MailRef mail)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Mails.TryGetValue((mail.Folder, mail.Uid), out var header)
            ? header
            : throw new MailNotFoundException(mail, "メールがフォルダーにありません。");
    }

    private MovedMailRef Move(MailRef mail, string to)
    {
        if (MoveFailure is not null)
        {
            throw MoveFailure;
        }

        if (!Folders.Contains(to))
        {
            throw new MailFolderNotFoundException(accountId, to);
        }

        if (!Mails.Remove((mail.Folder, mail.Uid), out var header))
        {
            throw new MailNotFoundException(mail, "メールがフォルダーにありません。");
        }

        var uid = _nextUid++;
        Mails[(to, uid)] = header with { Ref = header.Ref with { Folder = to, Uid = uid } };
        Moves.Add((mail, to));
        return new MovedMailRef(accountId, mail.Folder, to, ReturnCopyUid ? 888u : null, ReturnCopyUid ? uid : null);
    }
}

/// <summary>llama-server の代わり。起動・終了を記録し、落ちたことを真似られる。</summary>
internal sealed class FakeRuntime : ILlmRuntime
{
    private TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LlmProfile? Running { get; set; } = LlmProfile.Normal;
    public string ModelName => "test-model";
    public List<string> Calls { get; } = [];
    public Exception? StartFailure { get; set; }

    /// <summary>起動にかかる時間を真似る（キャンセルされたら OperationCanceledException）。</summary>
    public Func<CancellationToken, Task>? Loading { get; set; }

    public async Task StartAsync(LlmProfile profile, CancellationToken ct)
    {
        Calls.Add($"start:{profile}");
        if (Loading is { } loading)
        {
            await loading(ct);
        }

        if (StartFailure is { } failure)
        {
            throw failure;
        }

        Running = profile;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Calls.Add("stop");
        Running = null;
        return Task.CompletedTask;
    }

    public Task WaitForUnexpectedExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public void Crash()
    {
        Running = null;
        Interlocked.Exchange(ref _exit, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}

/// <summary>運転モードを手で切り替える。</summary>
internal sealed class FakeRunModes : IRunModeSource
{
    public RunModeState Current { get; private set; } = RunModeState.Running;

    public event EventHandler<RunModeState>? Changed;

    public void Set(RunMode mode, string? reason = null)
    {
        Current = new RunModeState(mode, reason, null);
        Changed?.Invoke(this, Current);
    }
}

internal sealed class FakeClassifier : IMailClassifier
{
    public Judgement Result { get; set; } = new(false, [], Confidence.High, "要約", "理由");
    public Func<MailSnapshot, Judgement>? ResultFor { get; set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public MailSnapshot? LastMail { get; private set; }
    public List<uint> SeenUids { get; } = [];

    public string PromptVersion => "p1";

    public Task<Judgement> ClassifyAsync(MailSnapshot mail, IReadOnlyList<ItemDefinition> items, CancellationToken ct)
    {
        Calls++;
        LastMail = mail;
        SeenUids.Add(mail.Header.Ref.Uid);
        return Failure is not null ? throw Failure : Task.FromResult(ResultFor?.Invoke(mail) ?? Result);
    }
}
