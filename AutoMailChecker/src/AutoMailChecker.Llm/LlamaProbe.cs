using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using AutoMailChecker.Core;

namespace AutoMailChecker.Llm;

/// <summary>起動済みの AI サーバーが判定を受け付けられるかを、/health と /v1/models で問い合わせる。</summary>
internal sealed class LlamaProbe(HttpMessageHandler handler) : IDisposable
{
    private const int ModelNameMaxLength = 100;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http = new(handler) { Timeout = RequestTimeout };

    /// <summary>判定を受け付けられればモデル名を返す。だめなら <see cref="ProbeException"/>。</summary>
    public async Task<string> ProbeAsync(Uri baseAddress, string? apiKey, CancellationToken ct)
    {
        // llama-server は読み込み中 503、準備できたら 200。/health がないサーバー（404）は次の /v1/models で確かめる
        bool hasHealth;
        using (var health = await GetAsync(baseAddress, "health", apiKey, ct).ConfigureAwait(false))
        {
            EnsureUsable(health.StatusCode, baseAddress);
            hasHealth = health.StatusCode != HttpStatusCode.NotFound;
        }

        using var models = await GetAsync(baseAddress, "v1/models", apiKey, ct).ConfigureAwait(false);
        EnsureUsable(models.StatusCode, baseAddress);
        if (models.StatusCode == HttpStatusCode.NotFound)
        {
            // どちらもなければ AI サーバーではない（同じポートで別のものが動いている、など）
            return hasHealth
                ? baseAddress.Authority
                : throw new ProbeException(ProbeFailure.Failed,
                    Translate.Text(
                        $"{Describe(baseAddress)} は AI サーバーではないようです（/health も /v1/models もありません）。接続先 URL を確かめてください。",
                        $"{Describe(baseAddress)} does not look like an AI server (it has neither /health nor /v1/models). Check the server URL."));
        }

        var text = await models.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ReadModelName(text) ?? baseAddress.Authority;
    }

    /// <summary>応答があるか（使っている間の見回り）。5xx と、繋がらない・時間切れは false。</summary>
    public async Task<bool> IsAliveAsync(Uri baseAddress, string? apiKey, CancellationToken ct)
    {
        try
        {
            using var response = await GetAsync(baseAddress, "health", apiKey, ct).ConfigureAwait(false);
            return (int)response.StatusCode < 500;
        }
        catch (ProbeException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>/v1/models の最初のモデルの名前。ファイルのパスなら拡張子なしのファイル名にする。</summary>
    internal static string? ReadModelName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0 || !data[0].TryGetProperty("id", out var idElement)
                || idElement.GetStringOrNull() is not { Length: > 0 } id)
            {
                return null;
            }

            var name = id.Trim();
            if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            return name.Length <= ModelNameMaxLength ? name : name[..ModelNameMaxLength];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri baseAddress, string path, string? apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, path));
        if (apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProbeException(ProbeFailure.Unreachable,
                Translate.Text(
                    $"{Describe(baseAddress)} に繋がりません。AI サーバーが起動しているか確かめてください（{ex.Message}）。",
                    $"Cannot reach {Describe(baseAddress)}. Check that the AI server is running ({ex.Message})."), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProbeException(ProbeFailure.Unreachable,
                Translate.Text(
                    $"{Describe(baseAddress)} が {RequestTimeout.TotalSeconds:0} 秒以内に応答しません。",
                    $"{Describe(baseAddress)} did not respond within {RequestTimeout.TotalSeconds:0} seconds."), ex);
        }
    }

    private static void EnsureUsable(HttpStatusCode status, Uri baseAddress)
    {
        switch (status)
        {
            case HttpStatusCode.ServiceUnavailable:
                throw new ProbeException(ProbeFailure.Loading, Translate.Text(
                    $"{Describe(baseAddress)} はモデルの読み込み中です。",
                    $"{Describe(baseAddress)} is loading its model."));
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new ProbeException(ProbeFailure.Rejected,
                    Translate.Text(
                        $"{Describe(baseAddress)} が API キーを受け付けませんでした。全般設定で API キーを登録してください。",
                        $"{Describe(baseAddress)} rejected the API key. Register the API key in General settings."));
            case HttpStatusCode.NotFound:
                return;
            case < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices:
                throw new ProbeException(ProbeFailure.Failed, Translate.Text(
                    $"{Describe(baseAddress)} がエラーを返しました（HTTP {(int)status}）。",
                    $"{Describe(baseAddress)} returned an error (HTTP {(int)status})."));
        }
    }

    internal static string Describe(Uri baseAddress) => baseAddress.GetLeftPart(UriPartial.Authority);
}

internal enum ProbeFailure
{
    /// <summary>繋がらない、または応答しない</summary>
    Unreachable,
    /// <summary>モデルの読み込み中</summary>
    Loading,
    /// <summary>API キーを受け付けない</summary>
    Rejected,
    /// <summary>そのほかのエラー</summary>
    Failed,
}

internal sealed class ProbeException(ProbeFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public ProbeFailure Failure { get; } = failure;
}
