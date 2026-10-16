using AutoMailChecker.Core;

namespace AutoMailChecker.Tests.Core;

public class SenderFilterTests
{
    private static readonly SenderFilter Filter = new(["noreply@example.com", "@ads.example.jp"]);

    [Theory]
    [InlineData("noreply@example.com", true)]
    [InlineData("NoReply@Example.COM", true)]
    [InlineData("Example News <noreply@example.com>", true)]
    [InlineData("\"Example, Inc.\" <NOREPLY@example.com>", true)]
    [InlineData("support@example.com", false)]
    public void アドレスは完全一致で_大文字小文字を区別しない(string from, bool expected)
    {
        Assert.Equal(expected, Filter.IsExcluded(from));
    }

    [Theory]
    [InlineData("campaign@ads.example.jp", true)]
    [InlineData("広告配信 <info@ADS.example.jp>", true)]
    [InlineData("info@sub.ads.example.jp", false)]
    [InlineData("info@example.jp", false)]
    public void ドメインは完全一致で照合する(string from, bool expected)
    {
        Assert.Equal(expected, Filter.IsExcluded(from));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("差出人不明")]
    public void アドレスが取れなければ除外しない(string from)
    {
        Assert.False(Filter.IsExcluded(from));
    }
}
