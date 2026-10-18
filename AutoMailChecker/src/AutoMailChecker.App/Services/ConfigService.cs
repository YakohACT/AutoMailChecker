using System.Text;
using System.Text.Json;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Services;

/// <summary>
/// config.jsonc の読み書き、検証、変更の通知。ファイルを監視し、直接編集されても再起動なしで反映する。
/// 初回起動時はコメント付きの雛形を書き出す。
/// </summary>
public sealed class ConfigService : IConfigProvider, IHostedService, IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppPaths _paths;
    private readonly ILogger<ConfigService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Timer _debounce;
    private readonly AppConfig _startup;
    private FileSystemWatcher? _watcher;
    private volatile Snapshot _snapshot;

    /// <summary>最後に読んだ、または書いたファイルの中身。自分の書き込みによる変更通知を無視するのに使う。</summary>
    private string? _lastKnownText;

    public ConfigService(AppPaths paths, ILogger<ConfigService> logger)
    {
        _paths = paths;
        _logger = logger;
        _snapshot = LoadInitial();
        _startup = _snapshot.Config;
        _debounce = new Timer(_ => _ = ReloadFromFileAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppConfig Current => _snapshot.Config;
    public IReadOnlyList<ConfigIssue> Issues => _snapshot.Issues;
    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var watcher = new FileSystemWatcher(_paths.ConfigDirectory, Path.GetFileName(_paths.ConfigFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += OnFileEvent;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _watcher = null;
        return Task.CompletedTask;
    }

    public async Task<ConfigSaveResult> SaveAsync(AppConfig config, CancellationToken ct)
    {
        var issues = ConfigValidator.Validate(config);
        if (issues.Count > 0)
        {
            return ConfigSaveResult.Rejected(issues);
        }

        var text = ConfigJson.Serialize(config);
        Snapshot previous;
        var next = new Snapshot(config, []);

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);
            if (File.Exists(_paths.ConfigFile))
            {
                File.Copy(_paths.ConfigFile, _paths.ConfigBackupFile, overwrite: true);
            }

            // 途中で落ちても壊れたファイルが残らないよう、一時ファイルに書いてから置き換える
            var temp = _paths.ConfigFile + ".tmp";
            await File.WriteAllTextAsync(temp, text, Utf8NoBom, ct).ConfigureAwait(false);
            File.Move(temp, _paths.ConfigFile, overwrite: true);
            _lastKnownText = text;

            previous = _snapshot;
            _snapshot = next;
        }
        finally
        {
            _fileLock.Release();
        }

        _logger.LogInformation("設定を保存しました。");
        var restartRequired = RequiresRestart(config);
        RaiseChanged(previous, next, restartRequired);
        return new ConfigSaveResult(true, [], restartRequired);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
        _fileLock.Dispose();
    }

    /// <summary>ファイルを読み直す。監視から呼ばれるほか、テストから直接呼ぶ。</summary>
    internal async Task ReloadFromFileAsync()
    {
        Snapshot previous;
        Snapshot next;

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var text = await ReadWithRetryAsync().ConfigureAwait(false);
            if (text is null || text == _lastKnownText)
            {
                return;
            }

            _lastKnownText = text;
            previous = _snapshot;
            var result = ConfigLoader.Load(text, previous.Config);
            LogIssues(result.Issues);
            next = new Snapshot(result.Config, result.Issues);
            _snapshot = next;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定ファイルの再読み込みに失敗しました。");
            return;
        }
        finally
        {
            _fileLock.Release();
        }

        _logger.LogInformation("設定ファイルの変更を反映しました。");
        RaiseChanged(previous, next, RequiresRestart(next.Config));
    }

    private Snapshot LoadInitial()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        if (!File.Exists(_paths.ConfigFile))
        {
            File.WriteAllText(_paths.ConfigFile, DefaultConfig.TemplateText, Utf8NoBom);
            _logger.LogInformation("設定ファイルを作成しました: {Path}", _paths.ConfigFile);
        }

        var text = File.ReadAllText(_paths.ConfigFile);
        _lastKnownText = text;
        var result = ConfigLoader.Load(text, DefaultConfig.Instance);
        LogIssues(result.Issues);
        return new Snapshot(result.Config, result.Issues);
    }

    private async Task<string?> ReadWithRetryAsync()
    {
        // エディターが書き込み中だと読めないことがあるので、少し待って読み直す
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(_paths.ConfigFile).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("設定ファイルが見つかりません。直前の設定のまま動きます: {Path}", _paths.ConfigFile);
                return null;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);

    /// <summary>アカウントと llm の endpoint・serverExe・model・port・ctxSize は再起動で反映する（外部仕様タブ）。</summary>
    private bool RequiresRestart(AppConfig config) =>
        JsonSerializer.Serialize(config.Accounts, ConfigJson.Options) != JsonSerializer.Serialize(_startup.Accounts, ConfigJson.Options)
        || config.Llm.Endpoint != _startup.Llm.Endpoint
        || config.Llm.ServerExe != _startup.Llm.ServerExe
        || config.Llm.Model != _startup.Llm.Model
        || config.Llm.Port != _startup.Llm.Port
        || config.Llm.CtxSize != _startup.Llm.CtxSize;

    private void RaiseChanged(Snapshot previous, Snapshot next, bool restartRequired)
    {
        try
        {
            Changed?.Invoke(this, new ConfigChangedEventArgs(previous.Config, next.Config, next.Issues, restartRequired));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定の変更通知の処理中にエラーが起きました。");
        }
    }

    private void LogIssues(IReadOnlyList<ConfigIssue> issues)
    {
        foreach (var issue in issues)
        {
            _logger.LogWarning("設定の誤り（{Section}）: {Issue}", issue.Section, issue);
        }
    }

    private sealed record Snapshot(AppConfig Config, IReadOnlyList<ConfigIssue> Issues);
}
