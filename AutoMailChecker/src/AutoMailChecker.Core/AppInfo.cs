namespace AutoMailChecker.Core;

/// <summary>アプリ全体で共有する名前。データフォルダーや資格情報の名前に使う。</summary>
public static class AppInfo
{
    public const string Name = "AutoMailChecker";

    /// <summary>IMAP の受信トレイ。名前の大文字小文字は区別しない（RFC 3501）。</summary>
    public const string InboxFolder = "INBOX";

    public static bool IsInbox(string folder) =>
        string.Equals(folder, InboxFolder, StringComparison.OrdinalIgnoreCase);
}
