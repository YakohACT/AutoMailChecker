using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Windows.Pause;

/// <summary>
/// 手動操作・ゲーム検知・全画面検知から運転モードを決める（クラス設計の PauseController）。
/// 検知は Poll を呼ぶたびに行う（既定 5 秒ごと）。監視用の常駐ソフトやフックは使わない。
/// 手動操作（トレイ）は UI スレッドから、Poll はバックグラウンドから呼ばれるので、内部で排他する。
/// </summary>
public sealed class PauseController(
    IConfigProvider config, ISystemSnapshotSource system, IGameLibraryLocator libraries, TimeProvider time,
    ILogger<PauseController> logger) : IRunModeController
{
    /// <summary>ゲームのインストール先を読み直す間隔（設定が変わったときも読み直す）。</summary>
    private static readonly TimeSpan LibraryRefreshInterval = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly RunModeResolver _resolver = new();
    private RunModeState _current = RunModeState.Running;
    private IReadOnlyList<PauseDetection> _detections = [];
    private GameLibraries _libraries = GameLibraries.Empty;
    private AppConfig? _librariesConfig;
    private DateTimeOffset _librariesLoadedAt = DateTimeOffset.MinValue;
    private bool _detectionErrorLogged;

    public event EventHandler<RunModeState>? Changed;

    public RunModeState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public IReadOnlyList<PauseDetection> CurrentDetections
    {
        get
        {
            lock (_gate)
            {
                return _detections;
            }
        }
    }

    /// <summary>停止対象を調べ直して運転モードを決める。</summary>
    public void Poll()
    {
        IReadOnlyList<PauseDetection>? detections = null;
        try
        {
            detections = Detect();
            _detectionErrorLogged = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 検知に失敗しても止めずに、前回の検知結果のまま動く
            if (!_detectionErrorLogged)
            {
                logger.LogWarning(ex, "停止対象の検知に失敗しました。前回の結果のまま動きます。");
                _detectionErrorLogged = true;
            }
        }

        Update(() =>
        {
            if (detections is not null)
            {
                _detections = detections;
            }
        });
    }

    public void PauseFor(TimeSpan? duration)
    {
        logger.LogInformation("トレイから一時停止しました（{Duration}）。", duration is { } d ? $"{d.TotalMinutes:0}分" : "再開するまで");
        Update(() => _resolver.PauseFor(duration, time.GetUtcNow()));
    }

    public void Stop()
    {
        logger.LogInformation("トレイから完全停止しました。");
        Update(_resolver.Stop);
    }

    public void Resume()
    {
        logger.LogInformation("トレイから再開しました。");
        Update(() => _resolver.Resume(_detections));
    }

    private void Update(Action change)
    {
        RunModeState? changed = null;
        lock (_gate)
        {
            change();
            var next = _resolver.Evaluate(time.GetUtcNow(), _detections,
                TimeSpan.FromSeconds(config.Current.Pause.ResumeDelaySeconds));
            if (next != _current)
            {
                logger.LogInformation("運転モード: {Old} → {New}{Reason}",
                    Label(_current.Mode), Label(next.Mode), next.Reason is { } reason ? $"（{reason}）" : "");
                _current = next;
                changed = next;
            }
        }

        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }
    }

    private IReadOnlyList<PauseDetection> Detect()
    {
        var settings = config.Current;
        var pause = settings.Pause;
        var needWindows = pause.Apps.Any(r => r.Enabled && !string.IsNullOrWhiteSpace(r.TitleContains));
        return ProcessRuleMatcher.Match(
            system.GetProcesses(),
            needWindows ? system.GetWindows() : [], // タイトルのルールがあるときだけウィンドウ一覧を取る
            pause,
            GetLibraries(settings),
            pause.FullscreenDetect && system.IsFullscreen());
    }

    private GameLibraries GetLibraries(AppConfig settings)
    {
        var now = time.GetUtcNow();
        if (!ReferenceEquals(settings, _librariesConfig) || now - _librariesLoadedAt >= LibraryRefreshInterval)
        {
            _libraries = libraries.Locate();
            _librariesConfig = settings;
            _librariesLoadedAt = now;
        }

        return _libraries;
    }

    private static string Label(RunMode mode) => mode switch
    {
        RunMode.Running => "稼働中",
        RunMode.LowLoad => "低負荷で継続",
        RunMode.Held => "判定保留",
        RunMode.Stopped => "完全停止",
        _ => mode.ToString(),
    };
}
