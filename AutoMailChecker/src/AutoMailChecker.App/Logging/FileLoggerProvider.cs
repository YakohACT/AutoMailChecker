using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Logging;

/// <summary>
/// %LOCALAPPDATA%\AutoMailChecker\logs\yyyy-MM-dd.log に日付別で書き、14 日を過ぎたものを消す。
/// メール本文は書かないこと（UID、件名の先頭 20 文字、判定結果、処理時間のみ）。
/// </summary>
public sealed class FileLoggerProvider(string directory, TimeProvider time, int retentionDays = FileLoggerProvider.DefaultRetentionDays)
    : ILoggerProvider
{
    public const int DefaultRetentionDays = 14;

    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = time.GetLocalNow();
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Abbreviate(level)}] {ShortCategory(category)}: {message}";
        lock (_gate)
        {
            try
            {
                var writer = GetWriter(DateOnly.FromDateTime(now.DateTime));
                writer.WriteLine(line);
                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }

                writer.Flush();
            }
            catch (IOException)
            {
                // ログが書けなくてもアプリは止めない
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private StreamWriter GetWriter(DateOnly date)
    {
        if (_writer is not null && date == _writerDate)
        {
            return _writer;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{date:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _writerDate = date;
        DeleteExpiredFiles(date);
        return _writer;
    }

    private void DeleteExpiredFiles(DateOnly today)
    {
        var cutoff = today.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
