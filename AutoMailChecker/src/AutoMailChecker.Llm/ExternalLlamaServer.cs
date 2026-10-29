using System.ComponentModel;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Llm;

/// <summary>
/// 接続先 URL（llm.endpoint）の起動済み AI サーバー（llama-server など OpenAI 互換のもの）を使う。アプリはサーバーを起動・終了しない。
/// - 接続先はこの PC だけ（設定の検証で 127.0.0.1・localhost 以外を拒む）。API キーは任意で、資格情報マネージャーから読む
/// - モデルの読み込み中（503）は 120 秒まで待つ。繋がらなければ 10 秒で諦める（呼び出し側が後でもう一度試す）
/// - 完全停止中は要求を送らないだけで、サーバーのメモリは空かない。低負荷の設定にも切り替えられないので、そのまま使う
/// - 使っている間は 30 秒ごとに /health を確かめ、続けて応答しなければ切れたとみなす
/// 接続先は起動時の設定を使い続ける（再起動で反映）。
/// </summary>
public sealed class ExternalLlamaServer : ILlmRuntime, ILlamaEndpoint, IDisposable
{
    /// <summary>見回りでこの回数続けて応答しなければ、切れたとみなす。</summary>
    private const int MaxMonitorFailures = 2;

    private readonly LlamaProbe _probe;
    private readonly ICredentialStore _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Timings _timings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Session? _session;
    private volatile string _modelName;
    private TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _lowLoadNoticed;

    public ExternalLlamaServer(
        IConfigProvider config, ICredentialStore credentials, TimeProvider time, ILogger<ExternalLlamaServer> logger)
        : this(config.Current.Llm.Endpoint, credentials, new SocketsHttpHandler { UseProxy = false }, time, logger, Timings.Default)
    {
    }

    internal ExternalLlamaServer(
        string endpoint, ICredentialStore credentials, HttpMessageHandler handler, TimeProvider time, ILogger logger, Timings timings)
    {
        BaseAddress = LlmEndpoint.TryParse(endpoint, out var error) ?? throw new ArgumentException(error, nameof(endpoint));
        _probe = new LlamaProbe(handler);
        _credentials = credentials;
        _time = time;
        _logger = logger;
        _timings = timings;
        _modelName = BaseAddress.Authority; // 繋いだらサーバーが返す名前にする
    }

    public Uri BaseAddress { get; }

    public LlmProfile? Running => _session?.Profile;

    public string ModelName => _modelName;

    public bool IsExternal => true;

    public LlamaConnection? Connection => _session is { } session ? new LlamaConnection(BaseAddress, session.ApiKey) : null;

    public async Task StartAsync(LlmProfile profile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { } current)
            {
                // 繋がっている。サーバーの設定は変えられないので、低負荷に切り替わってもそのまま使う
                current.Profile = profile;
                NoticeLowLoad(profile);
                return;
            }

            var apiKey = ReadApiKey();
            _modelName = await WaitUntilReadyAsync(apiKey, ct).ConfigureAwait(false);

            _lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new Session(profile, apiKey);
            _session = session;
            _ = MonitorAsync(session);
            _logger.LogInformation("AI サーバー（{Url}、モデル {Model}）に接続しました。", LlamaProbe.Describe(BaseAddress), _modelName);
            NoticeLowLoad(profile);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>要求を送るのをやめる（サーバーは起動したまま）。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _session?.Monitor.Cancel();
            _session = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>使っているサーバーに繋がらなくなるまで待つ（意図して止めたときは完了しない）。</summary>
    public Task WaitForUnexpectedExitAsync(CancellationToken ct) => _lost.Task.WaitAsync(ct);

    public void Dispose()
    {
        _session?.Monitor.Cancel();
        _session = null;
        _probe.Dispose();
    }

    /// <summary>資格情報マネージャーの API キー。登録されていなければ null（キーなしで送る）。</summary>
    internal static string? ReadApiKey(ICredentialStore credentials) =>
        credentials.ReadSecret(LlmEndpoint.ApiKeyCredentialKey) is { Length: > 0 } key ? key : null;

    private string? ReadApiKey()
    {
        try
        {
            return ReadApiKey(_credentials);
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning("API キーを資格情報マネージャーから読めませんでした。キーなしで繋ぎます: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>判定を受け付けられるようになるまで待ち、モデル名を返す。</summary>
    private async Task<string> WaitUntilReadyAsync(string? apiKey, CancellationToken ct)
    {
        var startedAt = _time.GetTimestamp();
        while (true)
        {
            try
            {
                return await _probe.ProbeAsync(BaseAddress, apiKey, ct).ConfigureAwait(false);
            }
            catch (ProbeException ex) when (ShouldKeepWaiting(ex.Failure, _time.GetElapsedTime(startedAt)))
            {
                // 読み込みが終わる、または起動するのを待つ
            }
            catch (ProbeException ex) when (ex.Failure == ProbeFailure.Loading)
            {
                throw new LlmStartException(
                    Translate.Text(
                        $"{LlamaProbe.Describe(BaseAddress)} のモデルの読み込みが {_timings.LoadingTimeout.TotalSeconds:0} 秒たっても終わりません。",
                        $"{LlamaProbe.Describe(BaseAddress)} is still loading its model after {_timings.LoadingTimeout.TotalSeconds:0} seconds."), ex);
            }
            catch (ProbeException ex)
            {
                throw new LlmStartException(ex.Message, ex);
            }

            await Task.Delay(_timings.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    private bool ShouldKeepWaiting(ProbeFailure failure, TimeSpan elapsed) => failure switch
    {
        ProbeFailure.Loading => elapsed < _timings.LoadingTimeout,
        ProbeFailure.Unreachable => elapsed < _timings.ConnectTimeout,
        _ => false,
    };

    /// <summary>使っている間、ときどき応答を確かめる。続けて応答しなければ切れたことにして知らせる。</summary>
    private async Task MonitorAsync(Session session)
    {
        var ct = session.Monitor.Token;
        var failures = 0;
        try
        {
            while (true)
            {
                await Task.Delay(failures == 0 ? _timings.MonitorInterval : _timings.PollInterval, _time, ct).ConfigureAwait(false);
                if (await _probe.IsAliveAsync(BaseAddress, session.ApiKey, ct).ConfigureAwait(false))
                {
                    failures = 0;
                    continue;
                }

                if (++failures < MaxMonitorFailures)
                {
                    continue;
                }

                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(_session, session))
                    {
                        return; // 止めた、または繋ぎ直した
                    }

                    _session = null;
                }
                finally
                {
                    _gate.Release();
                }

                _logger.LogWarning("AI サーバー（{Url}）が応答しなくなりました。", LlamaProbe.Describe(BaseAddress));
                _lost.TrySetResult();
                return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 止めた、または終了した
        }
    }

    private void NoticeLowLoad(LlmProfile profile)
    {
        if (profile == LlmProfile.LowLoad && !_lowLoadNoticed)
        {
            _lowLoadNoticed = true;
            _logger.LogInformation("接続先 URL の AI サーバーは低負荷の設定に切り替えられないので、そのまま判定を続けます。");
        }
    }

    /// <summary>
    /// 待ち時間（テストでは短くする）。
    /// </summary>
    /// <param name="PollInterval">準備を待つ間、または見回りで応答がなかったあとの確認の間隔</param>
    /// <param name="ConnectTimeout">繋がらないときに諦めるまでの時間</param>
    /// <param name="LoadingTimeout">モデルの読み込み中に待つ時間</param>
    /// <param name="MonitorInterval">使っている間の見回りの間隔</param>
    internal sealed record Timings(TimeSpan PollInterval, TimeSpan ConnectTimeout, TimeSpan LoadingTimeout, TimeSpan MonitorInterval)
    {
        public static Timings Default { get; } =
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30));
    }

    private sealed class Session(LlmProfile profile, string? apiKey)
    {
        private volatile int _profile = (int)profile;

        public LlmProfile Profile { get => (LlmProfile)_profile; set => _profile = (int)value; }
        public string? ApiKey { get; } = apiKey;
        public CancellationTokenSource Monitor { get; } = new();
    }
}
