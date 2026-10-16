using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Tests.Core;

public class RunModeResolverTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
    private static readonly PauseDetection Game = new("Steam:100", PauseAction.Stop, "Steamのゲームを検知（game.exe）");
    private static readonly PauseDetection Unity = new("app:Unity", PauseAction.LowLoad, "Unity起動中");

    private readonly RunModeResolver _resolver = new();

    [Fact]
    public void 何も検知しなければ稼働中()
    {
        Assert.Equal(RunModeState.Running, _resolver.Evaluate(Start, [], Delay));
    }

    [Fact]
    public void 条件が重なったら完全停止_判定保留_低負荷の順に強いほうを採る()
    {
        _resolver.PauseFor(null, Start);

        Assert.Equal(RunMode.Stopped, _resolver.Evaluate(Start, [Unity, Game], Delay).Mode);
        Assert.Equal(RunMode.Held, _resolver.Evaluate(Start.AddMinutes(5), [Unity], Delay).Mode);

        _resolver.Resume([]);
        var lowLoad = _resolver.Evaluate(Start.AddMinutes(6), [Unity], Delay);
        Assert.Equal(new RunModeState(RunMode.LowLoad, "Unity起動中", null), lowLoad);
    }

    [Fact]
    public void Unityを開いたままゲームを起動すれば完全停止_ゲームを閉じて60秒たつと低負荷で継続に戻る()
    {
        Assert.Equal(RunMode.Stopped, _resolver.Evaluate(Start, [Unity, Game], Delay).Mode);

        var justClosed = _resolver.Evaluate(Start.AddSeconds(5), [Unity], Delay);
        var waited = _resolver.Evaluate(Start.AddSeconds(65), [Unity], Delay);

        Assert.Equal(new RunModeState(RunMode.Stopped, "Steamのゲームを検知（game.exe）", null), justClosed);
        Assert.Equal(RunMode.LowLoad, waited.Mode);
    }

    [Fact]
    public void 低負荷の対象を閉じても60秒は低負荷のまま()
    {
        _resolver.Evaluate(Start, [Unity], Delay);

        Assert.Equal(RunMode.LowLoad, _resolver.Evaluate(Start.AddSeconds(59), [], Delay).Mode);
        Assert.Equal(RunMode.Running, _resolver.Evaluate(Start.AddSeconds(60), [], Delay).Mode);
    }

    [Fact]
    public void 再開までの待ちが0秒なら_検知が消えたらすぐ戻る()
    {
        _resolver.Evaluate(Start, [Game], TimeSpan.Zero);

        Assert.Equal(RunMode.Running, _resolver.Evaluate(Start.AddSeconds(1), [], TimeSpan.Zero).Mode);
    }

    [Fact]
    public void 一時停止は指定時間が過ぎたら解ける()
    {
        _resolver.PauseFor(TimeSpan.FromMinutes(30), Start);

        var held = _resolver.Evaluate(Start.AddMinutes(10), [], Delay);
        Assert.Equal(new RunModeState(RunMode.Held, null, Start.AddMinutes(30)), held);
        Assert.Equal(RunMode.Running, _resolver.Evaluate(Start.AddMinutes(30), [], Delay).Mode);
    }

    [Fact]
    public void 手動の完全停止は再開するまで続く()
    {
        _resolver.Stop();

        Assert.Equal(new RunModeState(RunMode.Stopped, RunModeResolver.ManualStopReason, null),
            _resolver.Evaluate(Start.AddHours(5), [], Delay));

        _resolver.Resume([]);
        Assert.Equal(RunMode.Running, _resolver.Evaluate(Start.AddHours(5), [], Delay).Mode);
    }

    [Fact]
    public void ゲーム中に手動で再開したら_そのゲームが終わるまでは止めない()
    {
        _resolver.Evaluate(Start, [Game], Delay);

        _resolver.Resume([Game]);

        Assert.Equal(RunMode.Running, _resolver.Evaluate(Start.AddSeconds(5), [Game], Delay).Mode);
        Assert.Equal(RunMode.Running, _resolver.Evaluate(Start.AddSeconds(10), [], Delay).Mode); // 終わった
        Assert.Equal(RunMode.Stopped, _resolver.Evaluate(Start.AddSeconds(15), [Game], Delay).Mode); // また起動したら止める
    }

    [Fact]
    public void 手動で再開しても_別のゲームを起動したら止める()
    {
        _resolver.Evaluate(Start, [Game], Delay);
        _resolver.Resume([Game]);
        var other = new PauseDetection("Epic Games:200", PauseAction.Stop, "Epic Gamesのゲームを検知（other.exe）");

        Assert.Equal(RunMode.Stopped, _resolver.Evaluate(Start.AddSeconds(5), [Game, other], Delay).Mode);
    }
}
