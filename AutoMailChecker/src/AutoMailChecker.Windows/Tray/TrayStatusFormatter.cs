using AutoMailChecker.Core;

namespace AutoMailChecker.Windows.Tray;

/// <summary>トレイのメニュー先頭の1行とツールチップの文言（画面設計 SC-01）。</summary>
public static class TrayStatusFormatter
{
    /// <summary>NotifyIcon.Text の上限。超えると例外になる。</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>例: 「稼働中 / 未処理 0件」「一時停止中（残り45分）/ 未処理 5件」</summary>
    public static string StatusLine(AppStatusSnapshot status, DateTimeOffset now)
    {
        var mode = status.Mode switch
        {
            RunMode.Running => Translate.Text("稼働中", "Running"),
            RunMode.LowLoad => WithReason(Translate.Text("低負荷で継続", "Low load"), status.ModeReason),
            RunMode.Held => status.HeldUntil is { } until
                ? Translate.Text($"一時停止中（残り{RemainingMinutes(until, now)}分）", $"Paused ({RemainingMinutes(until, now)} min left)")
                : Translate.Text("一時停止中（再開するまで）", "Paused (until resumed)"),
            RunMode.Stopped => WithReason(Translate.Text("完全停止", "Stopped"), status.ModeReason),
            _ => status.Mode.ToString(),
        };
        return Translate.Text($"{mode} / 未処理 {status.PendingCount}件", $"{mode} / {status.PendingCount} pending");
    }

    public static string Tooltip(AppStatusSnapshot status, DateTimeOffset now)
    {
        var lines = new List<string> { StatusLine(status, now) };
        if (status.Trial is { } trial)
        {
            lines.Add(Translate.Text($"お試し仕分け {trial.Processed}/{trial.Total}件", $"Trial sort {trial.Processed}/{trial.Total}"));
        }

        if (status.Errors.Count > 0)
        {
            var more = status.Errors.Count > 1
                ? Translate.Text($"（ほか{status.Errors.Count - 1}件）", $" (+{status.Errors.Count - 1} more)")
                : "";
            lines.Add(ErrorLine(status.Errors[0]) + more);
        }

        var text = string.Join("\n", lines);
        return text.Length <= MaxTooltipLength ? text : string.Concat(text.AsSpan(0, MaxTooltipLength - 1), "…");
    }

    /// <summary>エラー1件の表示（例:「AI: AI サーバーに接続できません…」）。</summary>
    public static string ErrorLine(StatusError error) => $"{SourceLabel(error.Source)}: {error.Message}";

    /// <summary>
    /// エラーの出どころの表示名。出どころの名前は各サービスが日本語で決めている（区別のためのキーでもある）ので、表示するときに訳す。
    /// アカウントのエラーはアカウント ID のまま。
    /// </summary>
    private static string SourceLabel(string source) => source switch
    {
        "設定" => Translate.Text("設定", "Settings"),
        "振り分け" => Translate.Text("振り分け", "Sorting"),
        _ => source,
    };

    private static string WithReason(string label, string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? label : Translate.Text($"{label}（{reason}）", $"{label} ({reason})");

    private static int RemainingMinutes(DateTimeOffset until, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Ceiling((until - now).TotalMinutes));
}
