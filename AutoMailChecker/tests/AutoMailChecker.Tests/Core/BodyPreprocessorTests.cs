using AutoMailChecker.Core;
using AutoMailChecker.Mail;

namespace AutoMailChecker.Tests.Core;

public class BodyPreprocessorTests
{
    private const int MaxChars = 4000;

    private readonly BodyPreprocessor _preprocessor = new(new AngleSharpHtmlTextExtractor());

    [Fact]
    public void HTMLだけのメールは_scriptやstyleや非表示の要素を除いてテキストにする()
    {
        const string html = """
            <html><head><style>p { color: red; }</style><title>件名</title></head>
            <body>
              <div style="display: none; max-height: 0">プリヘッダーの隠し文</div>
              <script>alert('x')</script>
              <p>ご予約が確定しました。</p>
              <p>宿泊日: 10月20日<br>人数: 2名</p>
              <img src="logo.png" alt="ホテルのロゴ">
            </body></html>
            """;

        var text = _preprocessor.ToPlainText(null, html, MaxChars);

        Assert.Equal("ご予約が確定しました。\n宿泊日: 10月20日\n人数: 2名\nホテルのロゴ", text);
    }

    [Fact]
    public void text_plainがあればHTMLより優先する()
    {
        var text = _preprocessor.ToPlainText("プレーンの本文", "<p>HTMLの本文</p>", MaxChars);

        Assert.Equal("プレーンの本文", text);
    }

    [Fact]
    public void 返信の引用と直前の見出し行を除く()
    {
        const string body = """
            了解しました。当日よろしくお願いします。

            2026年10月15日(木) 19:04 山田太郎 <taro@example.com>:
            > 日程の件、10月20日でいかがでしょうか。
            >
            > よろしくお願いします。
            """;

        Assert.Equal("了解しました。当日よろしくお願いします。", _preprocessor.ToPlainText(body, null, MaxChars));
    }

    [Fact]
    public void 英語の見出し行も除く()
    {
        const string body = "Thanks!\nOn Wed, Oct 15, 2026 at 7:04 PM Taro <taro@example.com> wrote:\n> Original";

        Assert.Equal("Thanks!", _preprocessor.ToPlainText(body, null, MaxChars));
    }

    [Fact]
    public void 転送された本文は残す()
    {
        const string body = """
            参考までに転送します。
            ---------- Forwarded message ---------
            From: 〇〇ホテル <info@example.com>
            > ご予約確定のお知らせ
            """;

        var text = _preprocessor.ToPlainText(body, null, MaxChars);

        Assert.Contains("> ご予約確定のお知らせ", text);
        Assert.Contains("From: 〇〇ホテル", text);
    }

    [Fact]
    public void 署名を除く()
    {
        const string body = "本文です。\n-- \n山田太郎\n03-0000-0000";

        Assert.Equal("本文です。", _preprocessor.ToPlainText(body, null, MaxChars));
    }

    [Fact]
    public void 署名の後ろに転送された本文があれば残す()
    {
        const string body = "確認お願いします。\n-- \n山田太郎\n---------- Forwarded message ---------\n請求書を送付します。";

        var text = _preprocessor.ToPlainText(body, null, MaxChars);

        Assert.DoesNotContain("山田太郎", text);
        Assert.Contains("請求書を送付します。", text);
    }

    [Fact]
    public void URLはドメインだけ残す()
    {
        const string body = "詳細は https://www.example.com/reserve?id=123&token=abc をご覧ください。";

        Assert.Equal("詳細は [リンク: www.example.com] をご覧ください。", _preprocessor.ToPlainText(body, null, MaxChars));
    }

    [Fact]
    public void 続く空白と空行を1つにまとめる()
    {
        const string body = "\n\n  一行目　　です\t\t。\n\n\n\n二行目  \n\n";

        Assert.Equal("一行目 です 。\n\n二行目", _preprocessor.ToPlainText(body, null, MaxChars));
    }

    [Fact]
    public void 見えない文字を除く()
    {
        Assert.Equal("セール開催中", _preprocessor.ToPlainText("セール​‌͏開催中﻿", null, MaxChars));
    }

    [Fact]
    public void 上限を超えたら先頭と末尾を残して中略する()
    {
        var body = new string('先', 3000) + new string('中', 1000) + new string('末', 1000);

        var text = _preprocessor.ToPlainText(body, null, MaxChars);

        Assert.Equal(new string('先', 3000) + "\n（中略）\n" + new string('末', 1000), text);
    }

    [Fact]
    public void 上限以内ならそのまま()
    {
        var body = new string('あ', MaxChars);

        Assert.Equal(body, _preprocessor.ToPlainText(body, null, MaxChars));
    }

    [Fact]
    public void 添付ファイル名は最大10件()
    {
        var names = Enumerable.Range(1, 12).Select(i => $"file{i}.pdf").Append(" ").ToList();

        var limited = BodyPreprocessor.LimitAttachmentNames(names);

        Assert.Equal(10, limited.Count);
        Assert.Equal("file1.pdf", limited[0]);
    }
}
