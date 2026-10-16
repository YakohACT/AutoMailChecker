using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoMailChecker.Core;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Llm;

/// <summary>llama-server の宛先と、起動ごとに作る API キー。止まっていれば ApiKey は null。</summary>
public interface ILlamaEndpoint
{
    Uri BaseAddress { get; }

    string? ApiKey { get; }
}

/// <summary>
/// IMailClassifier の llama-server 実装。指示文・メール・出力形式から要求を作って送り、結果を検証する（プロンプト設計タブ）。
/// - temperature 0、最大出力 512 トークン、思考モードなし、出力は JSON スキーマで固定
/// - 壊れた JSON、途中で切れた応答、時間切れ（300 秒）は1回だけ再試行し、だめなら判定失敗
/// - 知らない項目 ID は捨て、重複はまとめ、長すぎる要約と理由は切り詰める
/// </summary>
public sealed class LlamaClassifier : IMailClassifier, IDisposable
{
    /// <summary>最大出力トークン数。仕様書8章の 512 を採る（プロンプト設計タブの 256 と食い違うため、仕様書を正とした）。</summary>
    public const int MaxTokens = 512;

    public const int SummaryMaxLength = 60;
    public const int ReasonMaxLength = 120;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(300);

    private const int MaxAttempts = 2;

    private readonly ILlamaEndpoint _endpoint;
    private readonly PromptBuilder _prompts;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly TimeSpan _requestTimeout;
    private PreparedPrompt? _prepared;

    public LlamaClassifier(ILlamaEndpoint endpoint, ILogger<LlamaClassifier> logger)
        : this(endpoint, new PromptBuilder(), CreateHttpClient(), logger, DefaultRequestTimeout)
    {
    }

    internal LlamaClassifier(
        ILlamaEndpoint endpoint, PromptBuilder prompts, HttpClient http, ILogger logger, TimeSpan requestTimeout)
    {
        _endpoint = endpoint;
        _prompts = prompts;
        _http = http;
        _logger = logger;
        _requestTimeout = requestTimeout;
    }

    public string PromptVersion => PromptBuilder.PromptVersion;

    public async Task<Judgement> ClassifyAsync(MailSnapshot mail, IReadOnlyList<ItemDefinition> items, CancellationToken ct)
    {
        var prepared = Prepare(items);
        var body = BuildRequestBody(prepared, _prompts.BuildUserMessage(mail));

        string? lastProblem = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var (content, finishReason) = await SendAsync(body, ct).ConfigureAwait(false);
                if (finishReason == "length")
                {
                    throw new InvalidResponseException("応答が途中で切れました（最大出力トークン数に達した）。");
                }

                return Parse(content, prepared.SelectableIds);
            }
            catch (InvalidResponseException ex)
            {
                lastProblem = ex.Message;
                _logger.LogWarning("AI の出力が使えませんでした（{Attempt}/{Max} 回目）: {Problem}", attempt, MaxAttempts, ex.Message);
            }
        }

        throw new ClassificationException($"AI の出力が不正なため判定できませんでした: {lastProblem}");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>指示文と出力形式は項目の設定が変わったときだけ作り直す。</summary>
    private PreparedPrompt Prepare(IReadOnlyList<ItemDefinition> items)
    {
        var prepared = _prepared;
        if (prepared is not null && ReferenceEquals(prepared.Source, items))
        {
            return prepared;
        }

        prepared = new PreparedPrompt(
            items,
            PromptBuilder.BuildSystemPrompt(items),
            JsonSerializer.SerializeToElement(SchemaBuilder.Build(items)),
            PromptBuilder.SelectableItems(items).Select(i => i.Id).ToHashSet());
        _prepared = prepared;
        return prepared;
    }

    internal static string BuildRequestBody(PreparedPrompt prepared, string userMessage) =>
        JsonSerializer.Serialize(new
        {
            temperature = 0,
            max_tokens = MaxTokens,
            cache_prompt = true,
            // 思考モードを切る（起動引数の --reasoning off に加え、要求でも指定しておく）
            chat_template_kwargs = new { enable_thinking = false },
            messages = new[]
            {
                new { role = "system", content = prepared.SystemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "judgement", schema = prepared.Schema },
            },
        });

    private async Task<(string Content, string? FinishReason)> SendAsync(string body, CancellationToken ct)
    {
        var apiKey = _endpoint.ApiKey ?? throw new LlmUnavailableException("llama-server が起動していません。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint.BaseAddress, "v1/chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        string text;
        HttpStatusCode status;
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = response.StatusCode;
            text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidResponseException($"{_requestTimeout.TotalSeconds:0}秒以内に応答がありませんでした。");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"llama-server に接続できません: {ex.Message}", ex);
        }

        switch (status)
        {
            case HttpStatusCode.ServiceUnavailable:
                throw new LlmUnavailableException("llama-server はモデルの読み込み中です。");
            case HttpStatusCode.Unauthorized:
                throw new LlmUnavailableException("llama-server が API キーを受け付けませんでした（別の llama-server が同じポートで動いている可能性があります）。");
            case < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices:
                throw new InvalidResponseException($"llama-server がエラーを返しました（HTTP {(int)status}）: {Shorten(text)}");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var choice = document.RootElement.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            var finishReason = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
            return (content, finishReason);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidResponseException($"llama-server の応答の形が想定と違います: {ex.Message}");
        }
    }

    /// <summary>AI の出力（JSON）を検証して Judgement にする。</summary>
    internal Judgement Parse(string content, IReadOnlySet<ItemId> selectableIds)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.Trim());
        }
        catch (JsonException ex)
        {
            throw new InvalidResponseException($"JSON として読めません: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidResponseException("JSON のオブジェクトではありません。");
            }

            var isAdvertisement = Required(root, "is_advertisement").ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidResponseException("is_advertisement が真偽値ではありません。"),
            };

            var matched = new List<ItemId>();
            var itemsElement = Required(root, "matched_items");
            if (itemsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidResponseException("matched_items が配列ではありません。");
            }

            foreach (var element in itemsElement.EnumerateArray())
            {
                var id = new ItemId(element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString());
                if (!selectableIds.Contains(id))
                {
                    _logger.LogWarning("AI が知らない項目 ID を返したので捨てました: {ItemId}", id.Value);
                }
                else if (!matched.Contains(id))
                {
                    matched.Add(id);
                }
            }

            var confidence = Required(root, "confidence").GetStringOrNull() switch
            {
                "high" => Confidence.High,
                "medium" => Confidence.Medium,
                "low" => Confidence.Low,
                var other => throw new InvalidResponseException($"confidence が high / medium / low のどれでもありません: {other}"),
            };

            var summary = Required(root, "summary").GetStringOrNull()
                ?? throw new InvalidResponseException("summary が文字列ではありません。");
            var reason = Required(root, "reason").GetStringOrNull()
                ?? throw new InvalidResponseException("reason が文字列ではありません。");

            return new Judgement(isAdvertisement, matched, confidence,
                Truncate(summary.Trim(), SummaryMaxLength), Truncate(reason.Trim(), ReasonMaxLength));
        }
    }

    private static JsonElement Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidResponseException($"必須の属性 {name} がありません。");

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";

    private static HttpClient CreateHttpClient() =>
        // 127.0.0.1 宛てなのでプロキシを通さない。時間切れは要求ごとに決める
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

    internal sealed record PreparedPrompt(
        IReadOnlyList<ItemDefinition> Source, string SystemPrompt, JsonElement Schema, IReadOnlySet<ItemId> SelectableIds);

    /// <summary>AI の出力が使えない（再試行の対象）。</summary>
    private sealed class InvalidResponseException(string message) : Exception(message);
}

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
