using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Windows.Pause;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.Windows;

public class ProcessRuleMatcherTests
{
    private static readonly GameLibraries Libraries = new(
        [@"D:\SteamLibrary\steamapps\common", @"C:\Program Files (x86)\Steam\steamapps\common"],
        [@"C:\Program Files\Epic Games\Fortnite"]);

    private static readonly PauseConfig Pause = DefaultConfig.Instance.Pause with { DmmGameFolder = @"D:\DMMGames" };

    [Theory]
    [InlineData("osu!.exe", "osu!起動中", PauseAction.Stop)]
    [InlineData("OSU!.EXE", "osu!起動中", PauseAction.Stop)]
    [InlineData("Code.exe", "VS Code起動中", PauseAction.LowLoad)]
    [InlineData("devenv.exe", "Visual Studio起動中", PauseAction.LowLoad)]
    public void 実行ファイル名は完全一致で_大文字小文字を区別しない(string exe, string description, PauseAction action)
    {
        var detections = Match([Process(1, exe)]);

        var detection = Assert.Single(detections);
        Assert.Equal(description, detection.Description);
        Assert.Equal(action, detection.Action);
    }

    [Fact]
    public void 名前の一部だけ同じ実行ファイルは対象にしない()
    {
        Assert.Empty(Match([Process(1, "osu!helper.exe"), Process(2, "VSCode.exe")]));
    }

    [Fact]
    public void ウィンドウタイトルは部分一致()
    {
        var detections = Match([Process(1, "chrome.exe")], [new WindowInfo(1, "雀魂 -じゃんたま- - Google Chrome")]);

        Assert.Equal("雀魂起動中", Assert.Single(detections).Description);
    }

    [Fact]
    public void 両方を指定したルールは_同じプロセスで両方に一致したときだけ()
    {
        var otherJava = Match([Process(1, "javaw.exe")], [new WindowInfo(1, "Eclipse IDE")]);
        var titleOnOtherProcess = Match([Process(1, "javaw.exe"), Process(2, "notepad.exe")], [new WindowInfo(2, "Minecraft の攻略メモ")]);
        var minecraft = Match([Process(1, "javaw.exe")], [new WindowInfo(1, "Minecraft 1.21.4")]);

        Assert.Empty(otherJava);
        Assert.Empty(titleOnOtherProcess);
        Assert.Equal("Minecraft（Java版）起動中", Assert.Single(minecraft).Description);
    }

    [Fact]
    public void 無効にしたルールは使わない()
    {
        var pause = Pause with
        {
            Apps = [.. Pause.Apps.Select(r => r.Name == "osu!" ? r with { Enabled = false } : r)],
        };

        Assert.Empty(ProcessRuleMatcher.Match([Process(1, "osu!.exe")], [], pause, Libraries, fullscreen: false));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe", "Steamのゲームを検知（eldenring.exe）")]
    [InlineData(@"c:\program files (x86)\steam\steamapps\common\wallpaper_engine\wallpaper64.exe", "Steamのゲームを検知（wallpaper64.exe）")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", "Epic Gamesのゲームを検知（FortniteClient-Win64-Shipping.exe）")]
    [InlineData(@"D:\DMMGames\majsoul\jantama.exe", "DMMのゲームを検知（jantama.exe）")]
    public void 固定ルールはインストール先の配下にある実行ファイルで判定し_常に完全停止(string path, string description)
    {
        var detection = Assert.Single(Match([Process(42, Path.GetFileName(path), path)]));

        Assert.Equal(description, detection.Description);
        Assert.Equal(PauseAction.Stop, detection.Action);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe")]
    [InlineData(@"D:\DMMGames\DMMGamePlayer\DMMGamePlayer.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\EpicGamesLauncher.exe")]
    [InlineData(@"D:\SteamLibraryBackup\steamapps\common\game.exe")]
    public void ランチャー本体やライブラリの外は固定ルールの対象にしない(string path)
    {
        Assert.Empty(Match([Process(42, Path.GetFileName(path), path)]));
    }

    [Fact]
    public void 実行ファイルの場所が分からなくても_実行ファイル名のルールは使える()
    {
        Assert.Single(Match([Process(1, "RobloxPlayerBeta.exe", path: null)]));
    }

    [Fact]
    public void 全画面表示は設定でオンのときに完全停止として検知する()
    {
        var detection = Assert.Single(ProcessRuleMatcher.Match([], [], Pause, Libraries, fullscreen: true));

        Assert.Equal(new PauseDetection("fullscreen", PauseAction.Stop, "全画面表示を検知"), detection);
    }

    private static IReadOnlyList<PauseDetection> Match(IReadOnlyList<ProcessInfo> processes, IReadOnlyList<WindowInfo>? windows = null) =>
        ProcessRuleMatcher.Match(processes, windows ?? [], Pause, Libraries, fullscreen: false);

    private static ProcessInfo Process(int pid, string exe, string? path = @"C:\Apps\x.exe") =>
        new(pid, exe, path is null ? null : path == @"C:\Apps\x.exe" ? $@"C:\Apps\{exe}" : path);
}

public class GameLibraryLocatorTests
{
    [Fact]
    public void Steamのlibraryfolders_vdfからライブラリの場所を読む()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"228980"		"1234"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameLibraryLocator.ParseSteamLibraryFolders(vdf));
    }

    [Fact]
    public void EpicのマニフェストからInstallLocationを読む()
    {
        const string manifest = """
            { "FormatVersion": 0, "DisplayName": "Fortnite", "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite" }
            """;

        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", GameLibraryLocator.ParseEpicInstallLocation(manifest));
        Assert.Null(GameLibraryLocator.ParseEpicInstallLocation("{ broken"));
        Assert.Null(GameLibraryLocator.ParseEpicInstallLocation("{}"));
    }
}

public class PauseControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(Start);
    private readonly FakeSystem _system = new();
    private readonly PauseController _controller;
    private readonly List<RunModeState> _changes = [];

    public PauseControllerTests()
    {
        _controller = new PauseController(
            new FixedConfig(DefaultConfig.Instance), _system, new FakeLibraries(), _time, NullLogger<PauseController>.Instance);
        _controller.Changed += (_, state) => _changes.Add(state);
    }

    [Fact]
    public void ゲームを起動したら完全停止し_終了して60秒たったら再開する()
    {
        _system.Processes.Add(new ProcessInfo(10, "osu!.exe", @"C:\osu!\osu!.exe"));
        _controller.Poll();
        Assert.Equal(new RunModeState(RunMode.Stopped, "osu!起動中", null), _controller.Current);

        _system.Processes.Clear();
        _time.Advance(TimeSpan.FromSeconds(5));
        _controller.Poll();
        Assert.Equal(RunMode.Stopped, _controller.Current.Mode);

        _time.Advance(TimeSpan.FromSeconds(60));
        _controller.Poll();
        Assert.Equal(RunMode.Running, _controller.Current.Mode);
        Assert.Equal([RunMode.Stopped, RunMode.Running], _changes.Select(c => c.Mode));
    }

    [Fact]
    public void 開発ツールの起動中は低負荷で継続()
    {
        _system.Processes.Add(new ProcessInfo(11, "devenv.exe", @"C:\VS\devenv.exe"));

        _controller.Poll();

        Assert.Equal(new RunModeState(RunMode.LowLoad, "Visual Studio起動中", null), _controller.Current);
    }

    [Fact]
    public void トレイから一時停止_完全停止_再開ができる()
    {
        _controller.PauseFor(TimeSpan.FromMinutes(30));
        Assert.Equal(new RunModeState(RunMode.Held, null, Start.AddMinutes(30)), _controller.Current);

        _controller.Stop();
        Assert.Equal(RunMode.Stopped, _controller.Current.Mode);

        _controller.Resume();
        Assert.Equal(RunMode.Running, _controller.Current.Mode);
    }

    [Fact]
    public void 一時停止の時間が過ぎたら次の確認で再開する()
    {
        _controller.PauseFor(TimeSpan.FromHours(1));

        _time.Advance(TimeSpan.FromHours(1));
        _controller.Poll();

        Assert.Equal(RunMode.Running, _controller.Current.Mode);
    }

    [Fact]
    public void ゲーム中に再開したら_そのゲームの間は止めない()
    {
        _system.Processes.Add(new ProcessInfo(10, "osu!.exe", null));
        _controller.Poll();

        _controller.Resume();
        _time.Advance(TimeSpan.FromSeconds(5));
        _controller.Poll();

        Assert.Equal(RunMode.Running, _controller.Current.Mode);
        Assert.Single(_controller.CurrentDetections);
    }

    [Fact]
    public void タイトルのルールがあるときだけウィンドウ一覧を取る()
    {
        _controller.Poll();
        Assert.Equal(1, _system.WindowCalls); // 既定の設定には「雀魂」などタイトルのルールがある

        var noTitleRules = DefaultConfig.Instance with
        {
            Pause = DefaultConfig.Instance.Pause with { Apps = [new PauseAppRule { Name = "osu!", Exe = ["osu!.exe"] }] },
        };
        var system = new FakeSystem();
        new PauseController(new FixedConfig(noTitleRules), system, new FakeLibraries(), _time, NullLogger<PauseController>.Instance).Poll();
        Assert.Equal(0, system.WindowCalls);
    }

    [Fact]
    public void 検知に失敗しても前回の結果のまま動く()
    {
        _system.Processes.Add(new ProcessInfo(10, "osu!.exe", null));
        _controller.Poll();

        _system.Failure = new InvalidOperationException("一覧を取れません");
        _controller.Poll();

        Assert.Equal(RunMode.Stopped, _controller.Current.Mode);
    }

    private sealed class FakeSystem : ISystemSnapshotSource
    {
        public List<ProcessInfo> Processes { get; } = [];
        public List<WindowInfo> Windows { get; } = [];
        public bool Fullscreen { get; set; }
        public int WindowCalls { get; private set; }
        public Exception? Failure { get; set; }

        public IReadOnlyList<ProcessInfo> GetProcesses() => Failure is not null ? throw Failure : Processes.ToList();

        public IReadOnlyList<WindowInfo> GetWindows()
        {
            WindowCalls++;
            return Windows.ToList();
        }

        public bool IsFullscreen() => Fullscreen;
    }

    private sealed class FakeLibraries : IGameLibraryLocator
    {
        public GameLibraries Locate() => GameLibraries.Empty;
    }
}

public class WindowsSystemSnapshotTests
{
    [Fact]
    public void 動いているプロセスとその実行ファイルの場所を取れる()
    {
        var snapshot = new WindowsSystemSnapshot();

        var processes = snapshot.GetProcesses();
        var self = processes.Single(p => p.Pid == Environment.ProcessId);

        Assert.Equal(Path.GetFileName(Environment.ProcessPath), self.ExeName, ignoreCase: true);
        Assert.Equal(Environment.ProcessPath, self.Path, ignoreCase: true);
        Assert.Contains(snapshot.GetProcesses(), p => p.Pid == Environment.ProcessId); // 2回目は覚えた場所を使う
        _ = snapshot.GetWindows();
        _ = snapshot.IsFullscreen();
    }
}

public class ProcessingGateTests
{
    [Fact]
    public async Task 切り替えは処理中の1通が終わるのを待ち_その間は新しい1通を取らせない()
    {
        var gate = new AutoMailChecker.Services.ProcessingGate();
        Assert.True(gate.TryEnter());

        var hold = gate.HoldAsync(TestContext.Current.CancellationToken);
        Assert.False(hold.IsCompleted);
        gate.Exit();
        using (await hold)
        {
            Assert.False(gate.TryEnter());
        }

        Assert.True(gate.TryEnter());
    }
}
