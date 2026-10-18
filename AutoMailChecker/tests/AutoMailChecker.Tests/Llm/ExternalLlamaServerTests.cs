using System.Net;
using System.Net.Http;
using System.Text;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.Llm;

public sealed class ExternalLlamaServerTests : IDisposable
{
    private static readonly ExternalLlamaServer.Timings Fast = new(
        PollInterval: TimeSpan.FromMilliseconds(10),
        ConnectTimeout: TimeSpan.FromMilliseconds(100),
        LoadingTimeout: TimeSpan.FromSeconds(5),
        MonitorInterval: TimeSpan.FromMilliseconds(20));

    private readonly FakeServer _handler = new();
    private readonly MemoryCredentials _credentials = new();
    private readonly ExternalLlamaServer _server;

    public ExternalLlamaServerTests()
    {
        _server = new ExternalLlamaServer(
            "http://127.0.0.1:8080/v1", _credentials, _handler, TimeProvider.System, NullLogger.Instance, Fast);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task 繋いだらサーバーのモデル名を使い_登録した_API_キーを付けて判定する()
    {
        _credentials.Secrets[LlmEndpoint.ApiKeyCredentialKey] = "secret";
        _handler.Models = """{ "object": "list", "data": [ { "id": "C:\\models\\qwen3.5-9b\\Qwen3.5-9B-Q4_K_M.gguf" } ] }""";

        await _server.StartAsync(LlmProfile.Normal, Ct);

        Assert.Equal(LlmProfile.Normal, _server.Running);
        Assert.True(_server.IsExternal);
        Assert.Equal("Qwen3.5-9B-Q4_K_M", _server.ModelName);
        Assert.Equal(new LlamaConnection(new Uri("http://127.0.0.1:8080/"), "secret"), _server.Connection);
        Assert.All(_handler.Requests, r => Assert.Equal("Bearer secret", r.Authorization));
        Assert.Equal(["/health", "/v1/models"], _handler.Requests.Select(r => r.Path).Take(2));
    }

    [Fact]
    public async Task API_キーがなければ付けずに送る()
    {
        await _server.StartAsync(LlmProfile.Normal, Ct);

        Assert.Null(_server.Connection?.ApiKey);
        Assert.All(_handler.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task モデルの読み込み中は終わるまで待つ()
    {
        _handler.HealthStatuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _handler.HealthStatuses.Enqueue(HttpStatusCode.ServiceUnavailable);

        await _server.StartAsync(LlmProfile.Normal, Ct);

        Assert.Equal(LlmProfile.Normal, _server.Running);
        Assert.Equal(3, _handler.Requests.Count(r => r.Path == "/health"));
    }

    [Fact]
    public async Task 繋がらなければ短い時間で諦める()
    {
        _handler.Down = true;

        var ex = await Assert.ThrowsAsync<LlmStartException>(() => _server.StartAsync(LlmProfile.Normal, Ct));

        Assert.Contains("http://127.0.0.1:8080 に繋がりません", ex.Message);
        Assert.Null(_server.Running);
        Assert.Null(_server.Connection);
    }

    [Fact]
    public async Task API_キーを受け付けなければ待たずに諦める()
    {
        _handler.ModelsStatus = HttpStatusCode.Unauthorized;

        var ex = await Assert.ThrowsAsync<LlmStartException>(() => _server.StartAsync(LlmProfile.Normal, Ct));

        Assert.Contains("API キー", ex.Message);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task AI_サーバーでなければ使わない()
    {
        _handler.HealthStatuses.Enqueue(HttpStatusCode.NotFound);
        _handler.ModelsStatus = HttpStatusCode.NotFound;

        var ex = await Assert.ThrowsAsync<LlmStartException>(() => _server.StartAsync(LlmProfile.Normal, Ct));

        Assert.Contains("AI サーバーではない", ex.Message);
    }

    [Fact]
    public async Task 低負荷に切り替わっても繋ぎ直さない()
    {
        await _server.StartAsync(LlmProfile.Normal, Ct);
        var requests = _handler.Requests.Count(r => r.Path == "/v1/models");

        await _server.StartAsync(LlmProfile.LowLoad, Ct);

        Assert.Equal(LlmProfile.LowLoad, _server.Running);
        Assert.Equal(requests, _handler.Requests.Count(r => r.Path == "/v1/models"));
    }

    [Fact]
    public async Task 止めたら要求を送らず_予期しない終了とは扱わない()
    {
        await _server.StartAsync(LlmProfile.Normal, Ct);
        var lost = _server.WaitForUnexpectedExitAsync(Ct);

        await _server.StopAsync(Ct);
        _handler.Down = true;
        await Task.Delay(150, Ct);

        Assert.Null(_server.Running);
        Assert.Null(_server.Connection);
        Assert.False(lost.IsCompleted);
    }

    [Fact]
    public async Task 使っている間にサーバーが止まったら知らせる()
    {
        await _server.StartAsync(LlmProfile.Normal, Ct);
        var lost = _server.WaitForUnexpectedExitAsync(Ct);

        _handler.Down = true;
        await lost.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Null(_server.Running);
    }

    [Fact]
    public async Task 接続テストは保存前の_URL_で確かめる()
    {
        _handler.Models = """{ "data": [ { "id": "unsloth/Qwen3.5-9B-GGUF:Q4_K_M" } ] }""";
        var tester = new LlmConnectionTester(_credentials, () => _handler);

        Assert.Equal("接続できました（モデル: unsloth/Qwen3.5-9B-GGUF:Q4_K_M）。", await tester.TestAsync("http://localhost:8080", Ct));
        await Assert.ThrowsAsync<LlmStartException>(() => tester.TestAsync("http://192.168.1.20:8080", Ct));
        Assert.All(_handler.Requests, r => Assert.Equal("localhost", r.Host));
    }

    [Theory]
    [InlineData("""{ "data": [] }""", null)]
    [InlineData("""{ "data": [ { "id": "my-alias" } ] }""", "my-alias")]
    [InlineData("""{ "data": [ { "id": "/models/Qwen3.5-4B.Q8_0.gguf" } ] }""", "Qwen3.5-4B.Q8_0")]
    [InlineData("not json", null)]
    public void モデル名は_v1_models_の最初の_id_から取る(string json, string? expected)
    {
        Assert.Equal(expected, LlamaProbe.ReadModelName(json));
    }

    /// <summary>/health と /v1/models に答える、起動済みの AI サーバーの代わり。</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string Path, string Host, string? Authorization)> _requests = [];

        /// <summary>true にすると接続を拒む（サーバーが止まっている）。</summary>
        public volatile bool Down;

        /// <summary>/health が順に返す状態。空になったら 200。</summary>
        public Queue<HttpStatusCode> HealthStatuses { get; } = new();

        public HttpStatusCode ModelsStatus { get; set; } = HttpStatusCode.OK;
        public string Models { get; set; } = """{ "data": [ { "id": "test-model" } ] }""";

        public IReadOnlyList<(string Path, string Host, string? Authorization)> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_gate)
            {
                _requests.Add((path, request.RequestUri.Host, request.Headers.Authorization?.ToString()));
            }

            if (Down)
            {
                throw new HttpRequestException("対象のコンピューターによって拒否されたため、接続できませんでした。");
            }

            HttpResponseMessage response;
            lock (_gate)
            {
                response = path switch
                {
                    "/health" => new HttpResponseMessage(HealthStatuses.TryDequeue(out var status) ? status : HttpStatusCode.OK),
                    "/v1/models" => new HttpResponseMessage(ModelsStatus)
                    {
                        Content = new StringContent(Models, Encoding.UTF8, "application/json"),
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            return Task.FromResult(response);
        }

        // 接続テストは使い終わると破棄するが、同じものを何度も使う
        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        public Dictionary<string, string> Secrets { get; } = [];
        public string? ReadSecret(string key) => Secrets.GetValueOrDefault(key);
        public void WriteSecret(string key, string userName, string secret) => Secrets[key] = secret;
    }
}
