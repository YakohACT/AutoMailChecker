using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Core;

/// <summary>
/// 手動の指示と検知から運転モードを決める（仕様書10章「動作状態」）。入出力はしない。
/// - 複数の条件が重なったら、完全停止 → 判定保留 → 低負荷で継続 の順に強いほうを採る
/// - 検知が消えても、ゲームの再起動やロード画面での切り替わりに備えて、再開まで少し（既定 60 秒）待つ
/// - 一時停止は指定時間が過ぎたら解ける
/// - 手動で再開したときに検知していた完全停止の原因は、それが終わるまで無視する（新しく起動したものはまた止める）
/// スレッドセーフではない。呼び出し側で排他する。
/// </summary>
public sealed class RunModeResolver
{
    public static string ManualStopReason => Translate.Text("手動", "manual");

    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private ManualCommand _manual;
    private DateTimeOffset? _heldUntil;
    private DateTimeOffset? _stopSeenAt;
    private string? _stopReason;
    private DateTimeOffset? _lowLoadSeenAt;
    private string? _lowLoadReason;

    private enum ManualCommand
    {
        None,
        Held,
        Stopped,
    }

    public void PauseFor(TimeSpan? duration, DateTimeOffset now)
    {
        _manual = ManualCommand.Held;
        _heldUntil = duration is { } d ? now + d : null;
    }

    public void Stop()
    {
        _manual = ManualCommand.Stopped;
        _heldUntil = null;
    }

    public void Resume(IReadOnlyList<PauseDetection> currentDetections)
    {
        _manual = ManualCommand.None;
        _heldUntil = null;
        foreach (var detection in currentDetections.Where(d => d.Action == PauseAction.Stop))
        {
            _ignored.Add(detection.Key);
        }

        _stopSeenAt = null;
        _stopReason = null;
    }

    public RunModeState Evaluate(DateTimeOffset now, IReadOnlyList<PauseDetection> detections, TimeSpan resumeDelay)
    {
        if (_manual == ManualCommand.Held && _heldUntil is { } until && now >= until)
        {
            _manual = ManualCommand.None;
            _heldUntil = null;
        }

        // 無視していた原因が終わったら、次に起動したときはまた止める
        _ignored.RemoveWhere(key => detections.All(d => d.Key != key));

        var stop = detections.FirstOrDefault(d => d.Action == PauseAction.Stop && !_ignored.Contains(d.Key));
        if (stop is not null)
        {
            _stopSeenAt = now;
            _stopReason = stop.Description;
        }

        var lowLoad = detections.FirstOrDefault(d => d.Action == PauseAction.LowLoad);
        if (lowLoad is not null)
        {
            _lowLoadSeenAt = now;
            _lowLoadReason = lowLoad.Description;
        }

        var stopActive = stop is not null || (_stopSeenAt is { } stopSeen && now - stopSeen < resumeDelay);
        var lowLoadActive = lowLoad is not null || (_lowLoadSeenAt is { } lowSeen && now - lowSeen < resumeDelay);

        if (_manual == ManualCommand.Stopped)
        {
            return new RunModeState(RunMode.Stopped, ManualStopReason, null);
        }

        if (stopActive)
        {
            return new RunModeState(RunMode.Stopped, _stopReason, null);
        }

        if (_manual == ManualCommand.Held)
        {
            return new RunModeState(RunMode.Held, null, _heldUntil);
        }

        return lowLoadActive ? new RunModeState(RunMode.LowLoad, _lowLoadReason, null) : RunModeState.Running;
    }
}
