using System.Text.Json;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Tests.Core;

public class LlmEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData(" http://localhost:8080/v1/ ", "http://localhost:8080/")] // OpenAI 互換の書き方でもよい
    [InlineData("http://[::1]:8080", "http://[::1]:8080/")]
    [InlineData("https://127.0.0.1:8443/llm/v1", "https://127.0.0.1:8443/llm/")]
    public void この_PC_のアドレスなら要求の基点にする(string text, string expected)
    {
        var uri = LlmEndpoint.TryParse(text, out var error);

        Assert.Equal(expected, uri?.ToString());
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("https://api.example.com")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("ftp://127.0.0.1:8080")]
    [InlineData("http://user:pass@127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/?model=x")]
    [InlineData("")]
    public void ほかの_PC_や_URL_でないものは使わない(string text)
    {
        Assert.Null(LlmEndpoint.TryParse(text, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 既定は起動済みのサーバーに繋ぎ_書いていない設定ファイルも同じ()
    {
        Assert.Equal("http://127.0.0.1:8080", DefaultConfig.Instance.Llm.Endpoint);
        Assert.True(DefaultConfig.Instance.Llm.UsesEndpoint);

        var old = ConfigJson.Deserialize("""{ "llm": { "model": "D:\\models\\a.gguf" } }""");
        Assert.True(old.Llm.UsesEndpoint);

        var launch = ConfigJson.Deserialize("""{ "llm": { "endpoint": "", "model": "D:\\models\\a.gguf" } }""");
        Assert.False(launch.Llm.UsesEndpoint);
    }

    [Fact]
    public void 書き出すのは接続先だけで_使うかどうかは書かない()
    {
        using var json = JsonDocument.Parse(ConfigJson.Serialize(DefaultConfig.Instance));
        var llm = json.RootElement.GetProperty("llm");

        Assert.Equal("http://127.0.0.1:8080", llm.GetProperty("endpoint").GetString());
        Assert.False(llm.TryGetProperty("usesEndpoint", out _));
    }

    [Fact]
    public void 接続先があればモデルファイルは要らず_ほかの_PC_は誤り()
    {
        var external = DefaultConfig.Instance with
        {
            Llm = DefaultConfig.Instance.Llm with { Endpoint = "http://127.0.0.1:8080", ServerExe = "", Model = "" },
        };
        Assert.Empty(ConfigValidator.Validate(external));

        var remote = external with { Llm = external.Llm with { Endpoint = "http://192.168.1.20:8080" } };
        var issue = Assert.Single(ConfigValidator.Validate(remote));
        Assert.Equal("llm.endpoint", issue.Path);
        Assert.Contains("192.168.1.20", issue.Message);
    }

    [Fact]
    public void 接続先が空ならモデルファイルが要る()
    {
        var launch = DefaultConfig.Instance with
        {
            Llm = DefaultConfig.Instance.Llm with { Endpoint = "", Model = "" },
        };

        Assert.Equal(["llm.model"], ConfigValidator.Validate(launch).Select(i => i.Path));
    }

    [Fact]
    public void API_キーの資格情報はアカウントと重ならない名前にする()
    {
        Assert.Equal("AutoMailChecker/llm.apiKey", LlmEndpoint.ApiKeyCredentialKey);
        // アカウント ID に「.」は使えないので、どのアカウントの既定名とも重ならない
        Assert.Contains(ConfigValidator.Validate(DefaultConfig.Instance with
        {
            Accounts = [new AccountConfig { Id = "llm.apiKey", Host = "imap.gmail.com", User = "a@gmail.com" }],
            Items = [],
            Advertisement = new AdvertisementConfig(),
        }), i => i.Path == "accounts[0].id");
    }
}
