using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Persistence;

namespace AutoMailChecker.Tests;

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
