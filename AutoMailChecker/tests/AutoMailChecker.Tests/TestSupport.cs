using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Persistence;

namespace AutoMailChecker.Tests;

/// <summary>書かれたログを覚えておく ILogger（ログに本文が出ないことを確かめる）。</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
        {
            Messages.Add(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
        }
    }
}

/// <summary>保存すると本物と同じように検証し、通れば保存した設定を覚える IConfigProvider（ファイルには書かない）。</summary>
internal sealed class ValidatingConfig(AppConfig config) : IConfigProvider
{
    public AppConfig? Saved { get; private set; }
    public AppConfig Current => config;
    public IReadOnlyList<ConfigIssue> Issues => [];

    public event EventHandler<ConfigChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public Task<ConfigSaveResult> SaveAsync(AppConfig candidate, CancellationToken ct)
    {
        var issues = ConfigValidator.Validate(candidate);
        if (issues.Count > 0)
        {
            return Task.FromResult(ConfigSaveResult.Rejected(issues));
        }

        Saved = candidate;
        return Task.FromResult(new ConfigSaveResult(true, [], false));
    }
}

/// <summary>決まった設定を返すだけの IConfigProvider。</summary>
internal sealed class FixedConfig(AppConfig config) : IConfigProvider
{
    public AppConfig Current => config;
    public IReadOnlyList<ConfigIssue> Issues => [];

    public event EventHandler<ConfigChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public Task<ConfigSaveResult> SaveAsync(AppConfig config, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>テストから時刻を進められる TimeProvider。</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>テストごとの一時フォルダー。終わったら中身ごと消す。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutoMailChecker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        // SQLite の接続プールがファイルを掴んだままだと消せない
        SqliteStateStore.ReleasePooledConnections();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
