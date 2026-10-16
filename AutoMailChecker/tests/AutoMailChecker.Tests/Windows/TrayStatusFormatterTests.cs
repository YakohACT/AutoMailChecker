using AutoMailChecker.Core;
using AutoMailChecker.Windows.Tray;

namespace AutoMailChecker.Tests.Windows;

public class TrayStatusFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 稼働中()
    {
        Assert.Equal("稼働中 / 未処理 0件", TrayStatusFormatter.StatusLine(AppStatusSnapshot.Initial, Now));
    }

    [Fact]
    public void 低負荷で継続は理由も出す()
    {
        var status = AppStatusSnapshot.Initial with { Mode = RunMode.LowLoad, ModeReason = "VS Code起動中", PendingCount = 3 };

        Assert.Equal("低負荷で継続（VS Code起動中） / 未処理 3件", TrayStatusFormatter.StatusLine(status, Now));
    }

    [Fact]
    public void 一時停止は残り時間を分で切り上げて出す()
    {
        var status = AppStatusSnapshot.Initial with
        {
            Mode = RunMode.Held,
            HeldUntil = Now.AddMinutes(44).AddSeconds(30),
            PendingCount = 5,
        };

        Assert.Equal("一時停止中（残り45分） / 未処理 5件", TrayStatusFormatter.StatusLine(status, Now));
    }

    [Fact]
    public void ツールチップにはお試しの進み具合とエラーも出す()
    {
        var status = AppStatusSnapshot.Initial with
        {
            Mode = RunMode.Stopped,
            ModeReason = "Steamのゲームを検知",
            PendingCount = 12,
            Trial = new TrialProgress(1, 38, 120),
            Errors = [new StatusError("gmail2", "認証に失敗しました"), new StatusError("設定", "誤りがあります")],
        };

        var tooltip = TrayStatusFormatter.Tooltip(status, Now);

        Assert.Equal(
            "完全停止（Steamのゲームを検知） / 未処理 12件\nお試し仕分け 38/120件\ngmail2: 認証に失敗しました（ほか1件）",
            tooltip);
    }

    [Fact]
    public void ツールチップは127文字に収める()
    {
        var status = AppStatusSnapshot.Initial with { Errors = [new StatusError("設定", new string('長', 300))] };

        var tooltip = TrayStatusFormatter.Tooltip(status, Now);

        Assert.Equal(TrayStatusFormatter.MaxTooltipLength, tooltip.Length);
        Assert.EndsWith("…", tooltip);
    }
}
