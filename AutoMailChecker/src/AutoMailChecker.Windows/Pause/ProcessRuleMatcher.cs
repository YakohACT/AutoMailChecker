using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Windows.Pause;

/// <summary>
/// 停止対象の判定（仕様書10章「ゲーム・アプリの自動検知」）。入出力はしない。
/// - 固定ルール（常に完全停止、例外なし）: Steam・Epic・DMM のゲームのインストール先の配下にある実行ファイル。ランチャー本体は除く
/// - 指定ルール: 実行ファイル名（完全一致、大文字小文字は区別しない）、ウィンドウタイトル（部分一致）、両方（同じプロセスで両方に一致）
/// - 全画面表示（設定でオン/オフ）
/// </summary>
public static class ProcessRuleMatcher
{
    /// <summary>ランチャー本体は固定ルールの対象外。</summary>
    private static readonly string[] LauncherExePrefixes = ["DMMGamePlayer", "EpicGamesLauncher", "EpicWebHelper"];

    public static IReadOnlyList<PauseDetection> Match(
        IReadOnlyList<ProcessInfo> processes, IReadOnlyList<WindowInfo> windows, PauseConfig pause,
        GameLibraries libraries, bool fullscreen)
    {
        var detections = new List<PauseDetection>();
        string[] dmmRoots = string.IsNullOrWhiteSpace(pause.DmmGameFolder) ? [] : [pause.DmmGameFolder];

        foreach (var process in processes)
        {
            if (process.Path is not { } path || IsLauncher(process.ExeName))
            {
                continue;
            }

            var store = IsUnder(path, libraries.SteamGameRoots) ? "Steam"
                : IsUnder(path, libraries.EpicGameRoots) ? "Epic Games"
                : IsUnder(path, dmmRoots) ? "DMM"
                : null;
            if (store is not null)
            {
                detections.Add(new PauseDetection($"{store}:{process.Pid}", PauseAction.Stop, Translate.Text(
                    $"{store}のゲームを検知（{process.ExeName}）",
                    $"{store} game detected ({process.ExeName})")));
            }
        }

        foreach (var rule in pause.Apps.Where(r => r.Enabled))
        {
            if (MatchesRule(rule, processes, windows))
            {
                detections.Add(new PauseDetection($"app:{rule.Name}", rule.Mode, Translate.Text($"{rule.Name}起動中", $"{rule.Name} running")));
            }
        }

        if (fullscreen)
        {
            detections.Add(new PauseDetection("fullscreen", PauseAction.Stop, Translate.Text("全画面表示を検知", "Full screen detected")));
        }

        return detections;
    }

    private static bool MatchesRule(PauseAppRule rule, IReadOnlyList<ProcessInfo> processes, IReadOnlyList<WindowInfo> windows)
    {
        var exeNames = rule.Exe.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var title = rule.TitleContains?.Trim();
        var hasTitle = !string.IsNullOrEmpty(title);

        if (exeNames.Count > 0 && hasTitle)
        {
            // 両方: 実行ファイル名が一致したプロセスのウィンドウに、タイトルが含まれるときだけ（例: javaw.exe ＋「Minecraft」）
            var pids = processes.Where(p => exeNames.Contains(p.ExeName)).Select(p => p.Pid).ToHashSet();
            return windows.Any(w => pids.Contains(w.Pid) && w.Title.Contains(title!, StringComparison.OrdinalIgnoreCase));
        }

        if (exeNames.Count > 0)
        {
            return processes.Any(p => exeNames.Contains(p.ExeName));
        }

        return hasTitle && windows.Any(w => w.Title.Contains(title!, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnder(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            var normalized = root.TrimEnd('\\', '/') + "\\";
            if (path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLauncher(string exeName) =>
        LauncherExePrefixes.Any(prefix => exeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
