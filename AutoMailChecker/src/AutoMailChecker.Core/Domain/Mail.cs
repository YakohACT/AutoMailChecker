namespace AutoMailChecker.Core;

/// <summary>サーバー上の1通の位置。UIDVALIDITY が変わると無効になる。</summary>
public readonly record struct MailRef(string AccountId, string Folder, uint UidValidity, uint Uid);

/// <summary>除外判定に使うヘッダー。本文は含まない。</summary>
public sealed record MailHeader(MailRef Ref, string MessageId, string From, string Subject, DateTimeOffset Received);

/// <summary>AI へ渡す唯一のデータ（読み取り専用）。資格情報やフォルダー一覧は含めない。</summary>
public sealed record MailSnapshot(MailHeader Header, string BodyText, IReadOnlyList<string> AttachmentNames);
