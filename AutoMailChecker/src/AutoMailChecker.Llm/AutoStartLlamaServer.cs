using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Llm;

/// <summary>
/// 接続先 URL（llm.endpoint）を使いつつ、そこにサーバーが起動していなければアプリが llama-server を起動する（llm.manageServer）。
/// - 準備するとき、接続先で何かが待ち受けていればそれに繋ぐ（利用者が起動したサーバー。アプリは終了しない）
/// - 何も待ち受けていなければ、llm.serverExe と llm.model で、接続先 URL のポートに llama-server を起動する
///   （127.0.0.1 だけで待ち受け、API キーは起動のたびに乱数、Web UI は無効。<see cref="LlamaServerManager"/> と同じ）
/// - 止めるとき（完全停止、アプリの終了）、アプリが起動した llama-server は終了してメモリと VRAM を空ける。
///   利用者が起動したサーバーには要求を送るのをやめるだけ
/// どちらを使うかは準備のたびに確かめ直す。完全停止の間に利用者がサーバーを起動・終了していても、再開したときの状況に合わせる。
/// </summary>
public sealed class AutoStartLlamaServer : ILlmRuntime, ILlamaEndpoint, IDisposable
{
    private readonly ExternalLlamaServer _external;
    private readonly LlamaServerManager _own;
    private readonly LlamaProbe _probe;
    private readonly Uri _endpoint;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ILlmRuntime? _active;

    /// <summary>最後に使った（使おうとした）のが利用者のサーバーか。止めたあとの知らせや、失敗したあとの再試行の間隔に使う。</summary>
    private volatile bool _usingExternal;

    public AutoStartLlamaServer(
        IConfigProvider config, ICredentialStore credentials, string runDirectory, TimeProvider time, ILoggerFactory loggerFactory)
        : this(
            new ExternalLlamaServer(config, credentials, time, loggerFactory.CreateLogger<ExternalLlamaServer>()),
            config, runDirectory, time, loggerFactory, new SocketsHttpHandler { UseProxy = false })
    {
    }

    internal AutoStartLlamaServer(
        ExternalLlamaServer external, IConfigProvider config, string runDirectory, TimeProvider time,
        ILoggerFactory loggerFactory, HttpMessageHandler probeHandler)
    {
        _external = external;
        _endpoint = external.BaseAddress;
        _own = new LlamaServerManager(
            config, runDirectory, time, loggerFactory.CreateLogger<LlamaServerManager>(), port: _endpoint.Port);
        _probe = new LlamaProbe(probeHandler);
        _logger = loggerFactory.CreateLogger<AutoStartLlamaServer>();
    }

    public LlmProfile? Running => _active?.Running;

    public string ModelName => Current.ModelName;

    public bool IsExternal => _usingExternal;

    public LlamaConnection? Connection => (_active as ILlamaEndpoint)?.Connection;

    private ILlmRuntime Current => _usingExternal ? _external : _own;

    public async Task StartAsync(LlmProfile profile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = _active;
            if (active?.Running is null)
            {
                // 使っていない、または使っていたものが落ちた（切れた）。後始末をしてから、どちらを使うか確かめ直す
                _active = null;
                if (active is not null)
                {
                    await active.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                var listening = await _probe.IsListeningAsync(_endpoint, ct).ConfigureAwait(false);
                _usingExternal = listening;
                active = Current;
                _logger.LogInformation(listening
                        ? "{Url} で AI サーバーが起動しているので、それを使います（アプリは終了しません）。"
                        : "{Url} で AI サーバーが起動していないので、アプリが llama-server を起動します。",
                    LlamaProbe.Describe(_endpoint));
            }

            await active.StartAsync(profile, ct).ConfigureAwait(false);
            _active = active;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>アプリが起動した llama-server は終了する。利用者のサーバーには要求を送るのをやめる（サーバーは起動したまま）。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _active = null;
            await _external.StopAsync(ct).ConfigureAwait(false);
            await _own.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task WaitForUnexpectedExitAsync(CancellationToken ct) =>
        _active is { } active ? active.WaitForUnexpectedExitAsync(ct) : Task.Delay(Timeout.Infinite, ct);

    public void Dispose()
    {
        _active = null;
        _external.Dispose();
        _own.Dispose();
        _probe.Dispose();
    }
}
