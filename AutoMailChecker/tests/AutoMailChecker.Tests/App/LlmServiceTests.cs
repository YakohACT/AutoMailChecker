using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.App;

public sealed class LlmServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(Now);
    private readonly FakeRuntime _runtime = new() { Running = null };
    private readonly FakeRunModes _runModes = new();
    private readonly ProcessingGate _gate = new();
    private readonly AppStatusService _status = new(new FixedConfig(DefaultConfig.Instance));
    private readonly LlmService _service;

    public LlmServiceTests()
    {
        _service = new LlmService(_runtime, _runModes, _gate, new QueueSignal(), _status, _time, NullLogger<LlmService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 稼働中なら通常の設定で起動する()
    {
        await _service.ReconcileAsync(Ct);

        Assert.Equal(["start:Normal"], _runtime.Calls);
        Assert.Equal(LlmProfile.Normal, _runtime.Running);
    }

    [Fact]
    public async Task 低負荷で継続になったら_低負荷の設定で起動し直す()
    {
        _runtime.Running = LlmProfile.Normal;
        _runModes.Set(RunMode.LowLoad, "VS Code起動中");

        await _service.ReconcileAsync(Ct);

        Assert.Equal(["start:LowLoad"], _runtime.Calls);
    }

    [Fact]
    public async Task 完全停止なら終了してメモリを空け_再開したら起動する()
    {
        _runtime.Running = LlmProfile.Normal;
        _runModes.Set(RunMode.Stopped, "Steamのゲームを検知");
        await _service.ReconcileAsync(Ct);
        Assert.Null(_runtime.Running);

        _runModes.Set(RunMode.Running);
        await _service.ReconcileAsync(Ct);

        Assert.Equal(["stop", "start:Normal"], _runtime.Calls);
    }

    [Fact]
    public async Task 判定保留では起動したままにする()
    {
        _runtime.Running = LlmProfile.Normal;
        _runModes.Set(RunMode.Held);

        await _service.ReconcileAsync(Ct);

        Assert.Empty(_runtime.Calls);
        Assert.Equal(LlmProfile.Normal, _runtime.Running);
    }

    [Fact]
    public async Task 処理中の1通が終わってから終了する()
    {
        _runtime.Running = LlmProfile.Normal;
        Assert.True(_gate.TryEnter()); // 1通を処理中
        _runModes.Set(RunMode.Stopped);

        var reconcile = _service.ReconcileAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(reconcile.IsCompleted);
        Assert.Empty(_runtime.Calls);

        _gate.Exit();
        await reconcile;

        Assert.Equal(["stop"], _runtime.Calls);
        Assert.True(_gate.TryEnter()); // 切り替えが終われば、また処理できる
    }

    [Fact]
    public async Task 起動できなければ判定を保留して知らせ_30分後にもう一度試す()
    {
        _runtime.StartFailure = new LlmStartException("モデルファイルが見つかりません");

        var retryIn = await _service.ReconcileAsync(Ct);
        Assert.Equal(LlmService.RetryAfterFailure, retryIn);
        Assert.Contains(_status.Current.Errors, e => e.Source == LlmService.StatusSource);

        _time.Advance(TimeSpan.FromMinutes(10));
        await _service.ReconcileAsync(Ct);
        Assert.Single(_runtime.Calls); // 待っている間は試さない

        _runtime.StartFailure = null;
        _time.Advance(TimeSpan.FromMinutes(20));
        await _service.ReconcileAsync(Ct);
        Assert.Equal(LlmProfile.Normal, _runtime.Running);
        Assert.DoesNotContain(_status.Current.Errors, e => e.Source == LlmService.StatusSource);
    }

    [Fact]
    public async Task 接続先_URL_のサーバーに繋がらなければ_1分後にもう一度試す()
    {
        _runtime.IsExternal = true;
        _runtime.StartFailure = new LlmStartException("http://127.0.0.1:8080 に繋がりません。");

        var retryIn = await _service.ReconcileAsync(Ct);
        Assert.Equal(LlmService.RetryAfterExternalFailure, retryIn);
        Assert.Contains(_status.Current.Errors, e => e.Source == LlmService.StatusSource && e.Message.StartsWith("AI サーバーに接続できません", StringComparison.Ordinal));

        // 利用者がサーバーを起動したら、すぐ使い始める
        _runtime.StartFailure = null;
        _time.Advance(TimeSpan.FromMinutes(1));
        await _service.ReconcileAsync(Ct);
        Assert.Equal(LlmProfile.Normal, _runtime.Running);
        Assert.DoesNotContain(_status.Current.Errors, e => e.Source == LlmService.StatusSource);
    }

    [Fact]
    public async Task 読み込み中にゲームを起動したら_読み込みを打ち切る()
    {
        _runtime.Loading = ct => Task.Delay(Timeout.Infinite, ct);

        var reconcile = _service.ReconcileAsync(Ct);
        _runModes.Set(RunMode.Stopped, "osu!起動中");
        _service.OnModeChanged(null, _runModes.Current);
        await reconcile.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Null(_runtime.Running);
    }
}
