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
            RunMode.Running => "稼働中",
            RunMode.LowLoad => WithReason("低負荷で継続", status.ModeReason),
            RunMode.Held => status.HeldUntil is { } until
                ? $"一時停止中（残り{RemainingMinutes(until, now)}分）"
                : "一時停止中（再開するまで）",
            RunMode.Stopped => WithReason("完全停止", status.ModeReason),
            _ => status.Mode.ToString(),
        };
        return $"{mode} / 未処理 {status.PendingCount}件";
    }

    public static string Tooltip(AppStatusSnapshot status, DateTimeOffset now)
    {
        var lines = new List<string> { StatusLine(status, now) };
        if (status.Trial is { } trial)
        {
            lines.Add($"お試し仕分け {trial.Processed}/{trial.Total}件");
        }

        if (status.Errors.Count > 0)
        {
            var first = status.Errors[0];
            var more = status.Errors.Count > 1 ? $"（ほか{status.Errors.Count - 1}件）" : "";
            lines.Add($"{first.Source}: {first.Message}{more}");
        }

        var text = string.Join("\n", lines);
        return text.Length <= MaxTooltipLength ? text : string.Concat(text.AsSpan(0, MaxTooltipLength - 1), "…");
    }

    private static string WithReason(string label, string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? label : $"{label}（{reason}）";

    private static int RemainingMinutes(DateTimeOffset until, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Ceiling((until - now).TotalMinutes));
}
