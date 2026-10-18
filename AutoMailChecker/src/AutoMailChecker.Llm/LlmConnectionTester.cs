using System.ComponentModel;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Llm;

/// <summary>全般タブの AI の「接続テスト」。保存前の接続先 URL と、登録済みの API キーで /health と /v1/models を確かめる。</summary>
public sealed class LlmConnectionTester : ILlmConnectionTester
{
    private readonly ICredentialStore _credentials;
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public LlmConnectionTester(ICredentialStore credentials)
        : this(credentials, () => new SocketsHttpHandler { UseProxy = false })
    {
    }

    internal LlmConnectionTester(ICredentialStore credentials, Func<HttpMessageHandler> handlerFactory)
    {
        _credentials = credentials;
        _handlerFactory = handlerFactory;
    }

    public async Task<string> TestAsync(string endpoint, CancellationToken ct)
    {
        var baseAddress = LlmEndpoint.TryParse(endpoint, out var error) ?? throw new LlmStartException(error!);

        string? apiKey;
        try
        {
            apiKey = ExternalLlamaServer.ReadApiKey(_credentials);
        }
        catch (Win32Exception ex)
        {
            throw new LlmStartException($"API キーを資格情報マネージャーから読めません: {ex.Message}", ex);
        }

        using var probe = new LlamaProbe(_handlerFactory());
        try
        {
            var model = await probe.ProbeAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
            return $"接続できました（モデル: {model}{(apiKey is null ? "" : "、API キーあり")}）。";
        }
        catch (ProbeException ex)
        {
            throw new LlmStartException(ex.Message, ex);
        }
    }
}
