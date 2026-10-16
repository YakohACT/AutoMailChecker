using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.Llm;

public sealed class LlamaClassifierTests : IDisposable
{
    private static readonly IReadOnlyList<ItemDefinition> Items = DefaultConfig.Instance.Items;

    private readonly FakeEndpoint _endpoint = new();
    private readonly FakeHandler _handler = new();
    private readonly LlamaClassifier _classifier;

    public LlamaClassifierTests()
    {
        _classifier = new LlamaClassifier(
            _endpoint, new PromptBuilder(() => "1234567890123456"), new HttpClient(_handler),
            NullLogger.Instance, TimeSpan.FromMilliseconds(300));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _classifier.Dispose();

    [Fact]
    public async Task 応答を判定にし_要求は仕様どおりの形で送る()
    {
        _handler.Respond(Completion("""
            { "is_advertisement": false, "matched_items": ["important", "reservation"], "confidence": "high",
              "summary": "10月20日宿泊の予約確定。事前の情報登録が必要", "reason": "宿泊予約の確定通知で、事前登録を求めているため" }
            """));

        var judgement = await _classifier.ClassifyAsync(Mail(), Items, Ct);

        Assert.False(judgement.IsAdvertisement);
        Assert.Equal([new ItemId("important"), new ItemId("reservation")], judgement.MatchedItems);
        Assert.Equal(Confidence.High, judgement.Confidence);
        Assert.Equal("10月20日宿泊の予約確定。事前の情報登録が必要", judgement.Summary);

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal("http://127.0.0.1:8089/v1/chat/completions", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("cache_prompt").GetBoolean());
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(PromptBuilder.BuildSystemPrompt(Items), messages[0].GetProperty("content").GetString());
        Assert.StartsWith("<mail-1234567890123456>", messages[1].GetProperty("content").GetString());
        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var choices = format.GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").GetProperty("matched_items").GetProperty("items").GetProperty("enum");
        Assert.Equal(["important", "reservation", "payment", "payslip"], choices.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task 壊れたJSONは1回だけ再試行する()
    {
        _handler.Respond(Completion("{ \"is_advertisement\": fal"));
        _handler.Respond(Completion(Valid()));

        var judgement = await _classifier.ClassifyAsync(Mail(), Items, Ct);

        Assert.Equal(Confidence.Medium, judgement.Confidence);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task 二回とも使えなければ判定失敗()
    {
        _handler.Respond(Completion("ご質問ありがとうございます"));
        _handler.Respond(Completion("{}"));

        var ex = await Assert.ThrowsAsync<ClassificationException>(() => _classifier.ClassifyAsync(Mail(), Items, Ct));

        Assert.Contains("is_advertisement", ex.Message);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task 途中で切れた応答は再試行する()
    {
        _handler.Respond(Completion(Valid(), finishReason: "length"));
        _handler.Respond(Completion(Valid()));

        await _classifier.ClassifyAsync(Mail(), Items, Ct);

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task 時間切れは再試行し_だめなら判定失敗()
    {
        _handler.Respond(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        _handler.Respond(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ex = await Assert.ThrowsAsync<ClassificationException>(() => _classifier.ClassifyAsync(Mail(), Items, Ct));

        Assert.Contains("応答がありませんでした", ex.Message);
    }

    [Fact]
    public async Task 知らない項目IDと無効な項目は捨て_重複はまとめる()
    {
        IReadOnlyList<ItemDefinition> items = [.. Items.Take(3), Items[3] with { Enabled = false }];
        _handler.Respond(Completion(Valid("""["important", "unknown", "important", "payslip", "payment"]""")));

        var judgement = await _classifier.ClassifyAsync(Mail(), items, Ct);

        Assert.Equal([new ItemId("important"), new ItemId("payment")], judgement.MatchedItems);
    }

    [Fact]
    public async Task 長すぎる要約と理由は切り詰める()
    {
        var content = JsonSerializer.Serialize(new
        {
            is_advertisement = true,
            matched_items = Array.Empty<string>(),
            confidence = "low",
            summary = new string('要', 100),
            reason = new string('理', 200),
        });
        _handler.Respond(Completion(content));

        var judgement = await _classifier.ClassifyAsync(Mail(), Items, Ct);

        Assert.Equal(LlamaClassifier.SummaryMaxLength, judgement.Summary.Length);
        Assert.Equal(LlamaClassifier.ReasonMaxLength, judgement.Reason.Length);
        Assert.True(judgement.IsAdvertisement);
    }

    [Theory]
    [InlineData("""{ "is_advertisement": "no", "matched_items": [], "confidence": "high", "summary": "", "reason": "" }""")]
    [InlineData("""{ "is_advertisement": false, "matched_items": [], "confidence": "sure", "summary": "", "reason": "" }""")]
    [InlineData("""{ "is_advertisement": false, "matched_items": "important", "confidence": "high", "summary": "", "reason": "" }""")]
    [InlineData("""{ "is_advertisement": false, "matched_items": [], "confidence": "high", "summary": "" }""")]
    public async Task 形の違う出力は使わない(string content)
    {
        _handler.Respond(Completion(content));
        _handler.Respond(Completion(content));

        await Assert.ThrowsAsync<ClassificationException>(() => _classifier.ClassifyAsync(Mail(), Items, Ct));
    }

    [Fact]
    public async Task 起動していなければ判定失敗にせず_繋がらないことを知らせる()
    {
        _endpoint.ApiKey = null;

        await Assert.ThrowsAsync<LlmUnavailableException>(() => _classifier.ClassifyAsync(Mail(), Items, Ct));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task 接続できなければ繋がらないことを知らせる()
    {
        _handler.Respond((_, _) => throw new HttpRequestException("接続が拒否されました"));

        await Assert.ThrowsAsync<LlmUnavailableException>(() => _classifier.ClassifyAsync(Mail(), Items, Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task 読み込み中やキーの不一致は繋がらない扱い(HttpStatusCode status)
    {
        _handler.Respond((_, _) => Task.FromResult(new HttpResponseMessage(status)));

        await Assert.ThrowsAsync<LlmUnavailableException>(() => _classifier.ClassifyAsync(Mail(), Items, Ct));
    }

    [Fact]
    public async Task サーバーのエラーは再試行して判定失敗()
    {
        _handler.Respond((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        _handler.Respond((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<ClassificationException>(() => _classifier.ClassifyAsync(Mail(), Items, Ct));
        Assert.Equal(2, _handler.Requests.Count);
    }

    private static string Valid(string matchedItems = "[]") => $$"""
        { "is_advertisement": false, "matched_items": {{matchedItems}}, "confidence": "medium", "summary": "要約", "reason": "理由" }
        """;

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Completion(
        string content, string finishReason = "stop") =>
        (_, _) =>
        {
            var body = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finishReason } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        };

    private static MailSnapshot Mail() => new(
        new MailHeader(new MailRef("gmail1", "INBOX", 1, 10), "<m@example.com>", "〇〇ホテル <info@example.com>",
            "ご予約確定のお知らせ", new DateTimeOffset(2026, 10, 15, 10, 4, 0, TimeSpan.Zero)),
        "ご予約が確定しました。",
        []);

    private sealed class FakeEndpoint : ILlamaEndpoint
    {
        public Uri BaseAddress { get; } = new("http://127.0.0.1:8089/");
        public string? ApiKey { get; set; } = "test-key";
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        public void Respond(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return await _responses.Dequeue()(request, ct);
        }
    }
}
