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
