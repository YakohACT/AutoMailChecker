using System.Text;
using System.Text.RegularExpressions;

namespace AutoMailChecker.Core;

/// <summary>
/// AI に渡す本文を作る（プロンプト設計タブ「本文の前処理」）。上から順に当てる。
/// 1. text/plain を優先し、なければ HTML をテキスト化
/// 2. 返信の引用（行頭が「&gt;」の行と直前の見出し行）を除く。転送された本文は残す
/// 3. 署名（「-- 」だけの行から後ろ）を除く
/// 4. URL はドメインだけ残す
/// 5. 続く空白と空行を1つにまとめる
/// 6. 上限を超えたら先頭 3/4 と末尾 1/4 を残して間に「（中略）」を入れる
/// </summary>
public sealed partial class BodyPreprocessor(IHtmlTextExtractor htmlExtractor)
{
    public const int MaxAttachmentNames = 10;
    public const string OmissionMark = "（中略）";

    public string ToPlainText(string? plainText, string? html, int maxChars)
    {
        var text = !string.IsNullOrWhiteSpace(plainText) ? plainText
            : !string.IsNullOrWhiteSpace(html) ? htmlExtractor.ExtractText(html)
            : "";

        var lines = ZeroWidthChars().Replace(text, "").ReplaceLineEndings("\n").Split('\n');
        var kept = RemoveSignature(RemoveReplyQuotes(lines));
        var joined = ShortenUrls(string.Join('\n', kept));
        return Truncate(CollapseWhitespace(joined), maxChars);
    }

    /// <summary>添付ファイルは名前だけを最大10件渡す。中身は読まない。</summary>
    public static IReadOnlyList<string> LimitAttachmentNames(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Take(MaxAttachmentNames).ToList();

    internal static List<string> RemoveReplyQuotes(IReadOnlyList<string> lines)
    {
        var result = new List<string>(lines.Count);
        var forwarded = false;
        var inQuote = false;
        foreach (var line in lines)
        {
            // 転送の見出しより後ろは、引用の形でも判定に必要なので残す
            if (!forwarded && IsForwardMarker(line))
            {
                forwarded = true;
            }

            if (forwarded || !IsQuoted(line))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    inQuote = false;
                }

                result.Add(line);
                continue;
            }

            // 引用の始まりなら、直前の見出し行（「〜 wrote:」など）も除く
            if (!inQuote)
            {
                var previous = result.FindLastIndex(l => !string.IsNullOrWhiteSpace(l));
                if (previous >= 0 && IsQuoteHeader(result[previous]))
                {
                    result.RemoveRange(previous, result.Count - previous);
                }
            }

            inQuote = true;
        }

        return result;
    }

    internal static List<string> RemoveSignature(IReadOnlyList<string> lines)
    {
        var result = new List<string>(lines.Count);
        var inSignature = false;
        foreach (var line in lines)
        {
            if (line == "-- ")
            {
                inSignature = true;
                continue;
            }

            // 署名の後ろに転送された本文が続くときは、そこから先を残す
            if (inSignature && IsForwardMarker(line))
            {
                inSignature = false;
            }

            if (!inSignature)
            {
                result.Add(line);
            }
        }

        return result;
    }

    internal static string ShortenUrls(string text) =>
        UrlPattern().Replace(text, m =>
            Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? $"[リンク: {uri.Host}]"
                : "[リンク]");

    internal static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousBlank = true; // 先頭の空行も落とす
        foreach (var raw in text.Split('\n'))
        {
            var line = SpaceRun().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                if (!previousBlank)
                {
                    builder.Append('\n');
                }

                previousBlank = true;
                continue;
            }

            builder.Append(line).Append('\n');
            previousBlank = false;
        }

        return builder.ToString().TrimEnd('\n');
    }

    internal static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        // 請求や明細の要点は先頭に、配信停止の案内など広告の手がかりは末尾にありやすい
        var head = maxChars * 3 / 4;
        var tailStart = text.Length - (maxChars - head);
        if (head > 0 && char.IsHighSurrogate(text[head - 1]))
        {
            head--;
        }

        if (tailStart < text.Length && char.IsLowSurrogate(text[tailStart]))
        {
            tailStart++;
        }

        return $"{text[..head].TrimEnd()}\n{OmissionMark}\n{text[tailStart..].TrimStart()}";
    }

    private static bool IsQuoted(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('>') || trimmed.StartsWith('＞');
    }

    private static bool IsQuoteHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.EndsWith("wrote:", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("wrote：", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("書きました:", StringComparison.Ordinal)
            || trimmed.EndsWith("書きました：", StringComparison.Ordinal)
            // Gmail の日本語表示「2026年10月15日(木) 19:04 山田 <a@example.com>:」
            || trimmed.EndsWith(">:", StringComparison.Ordinal)
            || trimmed.EndsWith("＞：", StringComparison.Ordinal);
    }

    private static bool IsForwardMarker(string line) =>
        line.Contains("Forwarded message", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Begin forwarded message", StringComparison.OrdinalIgnoreCase)
        || line.Contains("転送メッセージ", StringComparison.Ordinal)
        || line.Contains("転送されたメッセージ", StringComparison.Ordinal);

    [GeneratedRegex(@"https?://[^\s<>""'（）「」『』【】]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[ \t 　]+")]
    private static partial Regex SpaceRun();

    // 広告メールのプリヘッダーに多い、見えない文字
    [GeneratedRegex(@"[­͏​-‍⁠﻿]")]
    private static partial Regex ZeroWidthChars();
}
