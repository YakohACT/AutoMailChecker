using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AutoMailChecker.Core;

namespace AutoMailChecker.Mail;

/// <summary>
/// HTML メールを AngleSharp でテキストにする。script、style、非表示の要素は除く。
/// ブロック要素と br は改行にし、空白の整理は BodyPreprocessor に任せる。
/// </summary>
public sealed class AngleSharpHtmlTextExtractor : IHtmlTextExtractor
{
    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "title", "meta", "link", "svg", "iframe", "object",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "center", "dd", "div", "dl", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr",
        "li", "main", "nav", "ol", "p", "pre", "section", "table", "tbody", "thead", "tfoot", "tr", "ul",
    };

    public string ExtractText(string html)
    {
        // HtmlParser はスレッドセーフと保証されていないので、呼び出しごとに作る
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        var builder = new StringBuilder(html.Length / 4);
        if (document.Body is { } body)
        {
            Walk(body, builder, inPre: false);
        }
        else if (document.DocumentElement is { } root)
        {
            Walk(root, builder, inPre: false);
        }

        return builder.ToString();
    }

    private static void Walk(INode node, StringBuilder builder, bool inPre)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text when inPre:
                    builder.Append(text.Data);
                    break;

                case IText text:
                    // 要素の間の改行やインデントだけのテキストで空行ができないよう、行頭の空白は捨てる
                    var inline = CollapseInline(text.Data);
                    if (builder.Length == 0 || builder[^1] == '\n')
                    {
                        inline = inline.TrimStart();
                    }

                    builder.Append(inline);
                    break;

                case IElement element:
                    AppendElement(element, builder, inPre);
                    break;
            }
        }
    }

    private static void AppendElement(IElement element, StringBuilder builder, bool inPre)
    {
        var tag = element.LocalName;
        if (SkippedTags.Contains(tag) || IsHidden(element))
        {
            return;
        }

        switch (tag)
        {
            case "br":
                builder.Append('\n');
                return;
            case "img":
                // 画像だけの広告メールでも手がかりが残るよう、代替テキストを拾う
                var alt = element.GetAttribute("alt");
                if (!string.IsNullOrWhiteSpace(alt))
                {
                    builder.Append(' ').Append(alt.Trim()).Append(' ');
                }

                return;
            case "td" or "th":
                builder.Append(' ');
                break;
        }

        var isBlock = BlockTags.Contains(tag);
        if (isBlock)
        {
            EnsureNewline(builder);
        }

        Walk(element, builder, inPre || tag.Equals("pre", StringComparison.OrdinalIgnoreCase));

        if (isBlock)
        {
            EnsureNewline(builder);
        }
    }

    private static bool IsHidden(IElement element)
    {
        if (element.HasAttribute("hidden"))
        {
            return true;
        }

        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
        {
            return false;
        }

        var compact = style.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return compact.Contains("display:none", StringComparison.Ordinal)
            || compact.Contains("visibility:hidden", StringComparison.Ordinal);
    }

    private static string CollapseInline(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousSpace = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                if (!previousSpace)
                {
                    builder.Append(' ');
                }

                previousSpace = true;
            }
            else
            {
                builder.Append(c);
                previousSpace = false;
            }
        }

        return builder.ToString();
    }

    private static void EnsureNewline(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }
}
