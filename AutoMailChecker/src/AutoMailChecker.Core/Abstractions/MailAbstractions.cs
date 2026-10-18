namespace AutoMailChecker.Core;

/// <summary>
/// メールの読み取り。アカウントごとに1つ。読み取りはすべて BODY.PEEK で行い、既読にしない。
/// </summary>
public interface IMailReader
{
    string AccountId { get; }

    Task<MailHeader> FetchHeaderAsync(MailRef mail, CancellationToken ct);

    Task<MailSnapshot> FetchSnapshotAsync(MailRef mail, int maxBodyChars, CancellationToken ct);

    /// <summary>受信日（サーバー側）が from〜to のメール。</summary>
    Task<IReadOnlyList<FoundMail>> SearchByDateAsync(string folder, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Message-ID でフォルダー内のメールを探す。見つからなければ null。移動の途中で落ちたときの確認に使う。</summary>
    Task<MailRef?> FindByMessageIdAsync(string folder, string messageId, CancellationToken ct);

    /// <summary>このアカウントにあるフォルダーの一覧（LIST、SPECIAL-USE 属性付き）。</summary>
    Task<IReadOnlyList<MailFolderInfo>> ListFoldersAsync(CancellationToken ct);
}

/// <summary>サーバー上のフォルダー1つ。</summary>
/// <param name="IsSpecial">送信済み・下書き・ごみ箱・すべてのメールなど、振り分け先に向かない特別なフォルダー</param>
/// <param name="CanSelect">メールを入れられるか（Gmail の「[Gmail]」のような入れ物だけのフォルダーは false）</param>
public sealed record MailFolderInfo(string Name, bool IsInbox, bool IsJunk, bool IsSpecial, bool CanSelect)
{
    /// <summary>振り分け先の候補にしてよいか（受信トレイ・迷惑メール・特別なフォルダーは除く）。</summary>
    public bool IsSortingCandidate => CanSelect && !IsInbox && !IsJunk && !IsSpecial;
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

/// <summary>設定にあるアカウントごとの読み取り口と移動口。アカウントは起動時の設定で決まる（追加・削除は再起動で反映）。</summary>
public interface IMailAccountRegistry
{
    IReadOnlyCollection<string> AccountIds { get; }

    bool TryGetReader(string accountId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMailReader? reader);

    bool TryGetMover(string accountId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IItemFolderMover? mover);
}

/// <summary>メールを移動できなかった（サーバーが MOVE にも UIDPLUS にも対応していない、移動先が迷惑メールフォルダー、など）。</summary>
public class MailMoveException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>振り分け先フォルダーが未設定、またはサーバーにない。メールは受信トレイに残し、その項目への移動を止めて知らせる。</summary>
public sealed class MailFolderNotFoundException(string accountId, string folder)
    : MailMoveException($"{accountId}: 振り分け先フォルダー「{folder}」がサーバーにありません。")
{
    public string AccountId { get; } = accountId;
    public string Folder { get; } = folder;
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
