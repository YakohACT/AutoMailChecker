namespace AutoMailChecker.Core;

/// <summary>
/// メールの読み取り。アカウントごとに1つ。読み取りはすべて BODY.PEEK で行い、既読にしない。
/// </summary>
public interface IMailReader
{
    string AccountId { get; }

    Task<MailHeader> FetchHeaderAsync(MailRef mail, CancellationToken ct);

    Task<MailSnapshot> FetchSnapshotAsync(MailRef mail, int maxBodyChars, CancellationToken ct);

    Task<IReadOnlyList<MailRef>> SearchByDateAsync(string folder, DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>
/// メールを変更する唯一の操作。アカウントごとに1つで、他アカウントの接続やフォルダーは持たない。
/// どのメソッドもフォルダー名を引数に取らず、移動先は実装側が設定の対応表から引く。
/// 削除・既読化・送信のメソッドは定義しない。
/// </summary>
public interface IItemFolderMover
{
    string AccountId { get; }

    Task<MovedMailRef> MoveToItemFolderAsync(MailRef mail, ItemId item, CancellationToken ct);

    Task<MovedMailRef> MoveToAdvertisementFolderAsync(MailRef mail, CancellationToken ct);

    /// <summary>
    /// 判定履歴画面でユーザーが「受信トレイに戻す」を押したときだけ呼ぶ（自動処理からは呼ばない）。
    /// DestUid がなければ messageId で移動先フォルダーを検索する。
    /// </summary>
    Task<UndoResult> MoveBackToInboxAsync(MovedMailRef moved, string? messageId, CancellationToken ct);
}

/// <summary>HTML メールをテキストにする。script、style、非表示の要素は除く。</summary>
public interface IHtmlTextExtractor
{
    string ExtractText(string html);
}

/// <summary>設定にあるアカウントごとの読み取り口。アカウントは起動時の設定で決まる（追加・削除は再起動で反映）。</summary>
public interface IMailAccountRegistry
{
    IReadOnlyCollection<string> AccountIds { get; }

    bool TryGetReader(string accountId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMailReader? reader);
}

/// <summary>
/// 対象のメールがサーバーにない。ユーザーが別のフォルダーへ移した、削除した、または UIDVALIDITY が変わって UID が無効になった。
/// 処理待ちから外すだけで、エラーとしては扱わない。
/// </summary>
public sealed class MailNotFoundException(MailRef mail, string reason)
    : Exception($"{mail.AccountId} {mail.Folder} UID {mail.Uid}: {reason}")
{
    public MailRef Mail { get; } = mail;
}

/// <summary>
/// IMAP サーバーに接続・認証できない、または通信が途中で切れた。
/// 認証の失敗（パスワード未登録を含む）は IsAuthenticationFailure が true になり、そのアカウントだけ止めて再設定を促す。
/// </summary>
public sealed class MailServerException(string accountId, string message, bool isAuthenticationFailure, Exception? inner = null)
    : Exception(message, inner)
{
    public string AccountId { get; } = accountId;
    public bool IsAuthenticationFailure { get; } = isAuthenticationFailure;
}
