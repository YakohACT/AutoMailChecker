using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Llm;

/// <summary>
/// llama-server.exe の起動・ヘルスチェック・終了（外部仕様タブ）。
/// - 127.0.0.1 だけで待ち受け、API キー必須、Web UI 無効で起動する
/// - API キーは起動のたびに乱数で作り、本人だけが読めるファイルで渡す。終了時に消す
/// - GET /health が 120 秒以内に 200 にならなければ失敗とし、3 回まで起動し直す
/// - 子プロセスはジョブオブジェクトに入れ、アプリ本体が異常終了しても一緒に終わるようにする
/// モデル・ポート・文脈長は起動時の設定を使い続ける（再起動で反映）。スレッド数などは次に起動したときに反映する。
/// </summary>
public sealed class LlamaServerManager : ILlmRuntime, ILlamaEndpoint, IDisposable
{
    public const int MaxStartAttempts = 3;
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly LlmConfig _startup;
    private readonly IConfigProvider _config;
    private readonly string _keyFile;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HttpClient _health = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JobObject? _job = JobObject.TryCreate();
    private readonly OutputTail _output = new(30);
    private volatile RunningState? _state;
    private volatile Process? _starting;
    private volatile bool _stopping;
    private TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LlamaServerManager(IConfigProvider config, string runDirectory, TimeProvider time, ILogger<LlamaServerManager> logger)
    {
        _config = config;
        _startup = config.Current.Llm;
        _keyFile = Path.Combine(runDirectory, "llama.key");
        _time = time;
        _logger = logger;
        BaseAddress = new Uri($"http://{LlamaArguments.ListenHost}:{_startup.Port}/");
    }

    public LlmProfile? Running => _state?.Profile;

    public string ModelName => Path.GetFileNameWithoutExtension(_startup.Model);

    public Uri BaseAddress { get; }

    public string? ApiKey => _state?.ApiKey;

    public async Task StartAsync(LlmProfile profile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is { } state && state.Profile == profile && !state.Process.HasExited)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            EnsureFilesExist();

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await StartOnceAsync(profile, ct).ConfigureAwait(false);
                    return;
                }
                catch (LlmStartException ex) when (attempt < MaxStartAttempts)
                {
                    _logger.LogWarning("llama-server を起動できませんでした（{Attempt}/{Max} 回目）: {Message}",
                        attempt, MaxStartAttempts, ex.Message);
                    await StopCoreAsync().ConfigureAwait(false);
                }
                catch
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>起動中の llama-server が予期せず終了するまで待つ（意図して止めたときは完了しない）。</summary>
    public Task WaitForUnexpectedExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);

    public void Dispose()
    {
        var process = _state?.Process ?? _starting;
        _stopping = true;
        _state = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(StopTimeout);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            process.Dispose();
        }

        DeleteKeyFile();
        _job?.Dispose();
        _health.Dispose();
    }

    private async Task StartOnceAsync(LlmProfile profile, CancellationToken ct)
    {
        EnsurePortAvailable();
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        WriteKeyFile(_keyFile, apiKey);

        var startInfo = new ProcessStartInfo(_startup.ServerExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_startup.ServerExe) ?? "",
        };
        foreach (var argument in LlamaArguments.Build(profile, _startup, _config.Current.Llm, _keyFile))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        // 出力は読み続けないと llama-server が止まるので、起動に失敗したときの手がかりとして最後の数行だけ持つ
        process.OutputDataReceived += (_, e) => _output.Add(e.Data);
        process.ErrorDataReceived += (_, e) => _output.Add(e.Data);
        process.Exited += OnProcessExited;
        _output.Clear();
        _stopping = false;
        _exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new LlmStartException($"llama-server を起動できません: {ex.Message}", ex);
        }

        _starting = process;
        if (_job?.TryAssign(process) != true)
        {
            _logger.LogWarning("llama-server をジョブオブジェクトに入れられませんでした。アプリが異常終了したときに残る可能性があります。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var startedAt = _time.GetTimestamp();
        await WaitForHealthAsync(process, ct).ConfigureAwait(false);

        _state = new RunningState(profile, apiKey, process);
        _starting = null;
        _logger.LogInformation("llama-server を起動しました（{Profile}、モデル {Model}、{Seconds:0} 秒）。",
            profile == LlmProfile.Normal ? "通常" : "低負荷", ModelName, _time.GetElapsedTime(startedAt).TotalSeconds);
    }

    private async Task WaitForHealthAsync(Process process, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + HealthTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new LlmStartException($"llama-server が終了しました（終了コード {process.ExitCode}）。{_output.Describe()}");
            }

            try
            {
                using var response = await _health.GetAsync(new Uri(BaseAddress, "health"), ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // まだ待ち受けていない
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // 1回の確認の時間切れ
            }

            if (_time.GetUtcNow() >= deadline)
            {
                throw new LlmStartException($"{HealthTimeout.TotalSeconds:0} 秒以内に準備ができませんでした。{_output.Describe()}");
            }

            await Task.Delay(HealthPollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task StopCoreAsync()
    {
        var process = _state?.Process ?? _starting;
        _stopping = true;
        _state = null;
        _starting = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(StopTimeout);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }

                _logger.LogInformation("llama-server を終了しました。");
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                _logger.LogWarning("llama-server の終了を確認できませんでした: {Message}", ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        DeleteKeyFile();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_stopping || !ReferenceEquals(sender, _state?.Process))
        {
            return; // 意図して止めた、または起動中（起動側で検知する）
        }

        _state = null;
        _logger.LogWarning("llama-server が予期せず終了しました。{Output}", _output.Describe());
        _exited.TrySetResult();
    }

    private void EnsureFilesExist()
    {
        if (!File.Exists(_startup.ServerExe))
        {
            throw new LlmStartException($"llama-server.exe が見つかりません（llm.serverExe: {_startup.ServerExe}）。");
        }

        if (!File.Exists(_startup.Model))
        {
            throw new LlmStartException($"モデルファイルが見つかりません（llm.model: {_startup.Model}）。");
        }
    }

    private void EnsurePortAvailable()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _startup.Port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException)
        {
            throw new LlmStartException(
                $"ポート {_startup.Port} はほかのプログラムが使っています。残っている llama-server を終了するか、llm.port を変えてください。");
        }
    }

    /// <summary>API キーのファイルを、本人だけが読める権限で作る。</summary>
    internal static void WriteKeyFile(string path, string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        var bytes = Encoding.ASCII.GetBytes(apiKey + "\n");

        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, bytes);
            return;
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("現在のユーザーを特定できません。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // 親フォルダーの権限を引き継がない
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        using var stream = new FileInfo(path).Create(
            FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(bytes);
    }

    private void DeleteKeyFile()
    {
        try
        {
            File.Delete(_keyFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("API キーのファイルを消せませんでした: {Message}", ex.Message);
        }
    }

    private sealed record RunningState(LlmProfile Profile, string ApiKey, Process Process);

    /// <summary>llama-server の出力の最後の数行。</summary>
    private sealed class OutputTail(int capacity)
    {
        private readonly Queue<string> _lines = new();
        private readonly object _gate = new();

        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > capacity)
                {
                    _lines.Dequeue();
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lines.Clear();
            }
        }

        public string Describe(int count = 8)
        {
            lock (_gate)
            {
                return _lines.Count == 0 ? "" : "最後の出力: " + string.Join(" / ", _lines.TakeLast(count));
            }
        }
    }
}
